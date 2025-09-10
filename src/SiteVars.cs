using Landis.Core;
using Landis.SpatialModeling;
using Landis.Library.UniversalCohorts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.IO;
using static Landis.Extension.Disturbance.DiseaseProgression.Auxiliary;
using Landis.Library.Succession.DemographicSeeding;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public static class SiteVars
    {
        private static ISiteVar<SiteCohorts> universalCohorts;
        private static Dictionary<ISpecies, HostIndex> speciesHostIndex;
        private static int dispersalProbabilityMatrixWidth;
        private static int dispersalProbabilityMatrixHeight;
        private static double[] indexOffsetDispersalProbability;
        private static (int x, int y) landscapeDimensions;
        private static int[] resproutLifetime;
        private static (int x, int y) worstCaseMaximumDispersalCellDistance;
        //NOTE: no longer necessary to exist
        private static List<(int x, int y)> precalculatedDispersalDistanceOffsets;
        //TODO: Add to input parameters
        private static int resproutMaxLongevity;
        private static double[] susceptibleProbability;
        private static double[] infectedProbability;
        private static double[] diseasedProbability;
        private static IInputParameters parameters;
        private const int MAX_IMAGE_SIZE = 16384;
        private static SHIMode siteHostIndexMode = SHIMode.Mean;
        public static void Initialize(ICore modelCore, IInputParameters inputParameters) {
            parameters = inputParameters;
            universalCohorts = PlugIn.ModelCore.GetSiteVar<SiteCohorts>("Succession.UniversalCohorts");
            landscapeDimensions = (PlugIn.ModelCore.Landscape.Dimensions.Columns, PlugIn.ModelCore.Landscape.Dimensions.Rows);
            speciesHostIndex = parameters.SpeciesHostIndex;

            susceptibleProbability = new double[landscapeDimensions.x * landscapeDimensions.y];
            infectedProbability = new double[landscapeDimensions.x * landscapeDimensions.y];
            diseasedProbability = new double[landscapeDimensions.x * landscapeDimensions.y];
            /* foreach (var species in PlugIn.ModelCore.Species) {
                speciesNameToISpecies[species.Name] = species;
            } */

            int worstCaseMaximumDispersalCellDistanceX = (int)Math.Ceiling(parameters.DispersalMaxDistance / PlugIn.ModelCore.CellLength);
            worstCaseMaximumDispersalCellDistance = (worstCaseMaximumDispersalCellDistanceX, (int)(worstCaseMaximumDispersalCellDistanceX * 0.7071067812) + 1);
            
            precalculatedDispersalDistanceOffsets = new List<(int x, int y)>();
            for (int x = -worstCaseMaximumDispersalCellDistance.x; x <= worstCaseMaximumDispersalCellDistance.x; x++) {
                for (int y = -worstCaseMaximumDispersalCellDistance.y; y <= worstCaseMaximumDispersalCellDistance.y; y++) {
                    if ((x == 0 && y == 0) || CalculatedEuclideanDistanceUsingGridOffset(x, y) > worstCaseMaximumDispersalCellDistance.x) continue;
                    precalculatedDispersalDistanceOffsets.Add((x, y));
                }
            };
            PlugIn.ModelCore.UI.WriteLine($"Generating dispersal lookup matrix for {LandscapeDimensions.x}x{LandscapeDimensions.y} landscape");
            dispersalProbabilityMatrixWidth = worstCaseMaximumDispersalCellDistance.x + 1;
            dispersalProbabilityMatrixHeight = (int)(worstCaseMaximumDispersalCellDistance.x * 0.7071067812) + 1;
            indexOffsetDispersalProbability = GenerateDispersalProbabilityMatrix(parameters.DispersalProbabilityKernel, parameters.AlphaCoefficient, PlugIn.ModelCore.CellLength, worstCaseMaximumDispersalCellDistance.x, parameters.DispersalMaxDistance);
            PlugIn.ModelCore.UI.WriteLine("Generating dispersal probability matrix image");
            GenerateProbabilityMatrixImage(indexOffsetDispersalProbability);
            //TODO: Initializes empty for now, but realistically the spinup cycle should add some sites to this
            resproutLifetime = new int[LandscapeDimensions.x *LandscapeDimensions.y];
            //TODO: Add to input parameters
            resproutMaxLongevity = 5/* parameters.ResproutMaxLongevity */;
            //TODO: Add to input parameters
            PlugIn.ModelCore.UI.WriteLine($"Finished generating dispersal lookup matrix for {LandscapeDimensions.x}x{LandscapeDimensions.y} landscape");
        }
        public static SHIMode SHIMode {
            get {
                return siteHostIndexMode;
            }
            set {
                siteHostIndexMode = value;
            }
        }
        public static int DispersalProbabilityMatrixWidth {
            get {
                return dispersalProbabilityMatrixWidth;
            }
        }
        public static int DispersalProbabilityMatrixHeight {
            get {
                return dispersalProbabilityMatrixHeight;
            }
        }
        public static int[] ResproutLifetime {
            get {
                return resproutLifetime;
            }
        }

        public static List<(int x, int y)> PrecalculatedDispersalDistanceOffsets {
            get {
                return precalculatedDispersalDistanceOffsets;
            }
        }
        public static (int x, int y) LandscapeDimensions {
            get {
                return landscapeDimensions;
            }
        }

        public static (int x, int y) GetWorstCaseMaximumUniformDispersalDistance() {
            return worstCaseMaximumDispersalCellDistance;
        }
        public static void DecrementResproutLifetimes() {
            for (int x = 0; x < LandscapeDimensions.x; x++) {
                for (int y = 0; y < LandscapeDimensions.y; y++) {
                    int index = CalculateCoordinatesToIndex(x, y, LandscapeDimensions.x);
                    if (resproutLifetime[index] > 0) resproutLifetime[index]--;
                }
            }
        }
        public static void AddResproutLifetime(int x, int y) {
            //TODO: This is a placeholder, determine a better way to implement lifetime
            int lifetime = resproutMaxLongevity;
            int index = CalculateCoordinatesToIndex(x, y, LandscapeDimensions.x);
            resproutLifetime[index] = Math.Min(resproutLifetime[index] + lifetime, resproutMaxLongevity);    
        }
        
        /* public static double CalculateTransmissionAndWeatherIndex() {

        } */
        
        public static double CalculateSiteHostIndexMean(ActiveSite site) {
            int divisor = 0;
            double sum = 0.0;
            foreach (ISpeciesCohorts speciesCohorts in universalCohorts[site]) {
                if (!speciesHostIndex.TryGetValue(speciesCohorts.Species, out HostIndex hostIndex)) {
                    continue;
                }
                ushort oldest = Util.GetMaxAge(speciesCohorts);
                if (oldest == 0) continue;
                sum += CalculateHostIndex(hostIndex, oldest);
                divisor++;
            }
            
            if (divisor == 0) return 0.0f;
            return sum / divisor;
        }
        public static void SetDefaultProbabilities(List<int> healthySitesListIndices, List<int> infectedSitesListIndices/* , List<int> ignoredSitesListIndices */) {
            foreach (int index in healthySitesListIndices) {
                susceptibleProbability[index] = 1.0;
                infectedProbability[index] = 0.0;
                diseasedProbability[index] = 0.0;
            }
            foreach (int index in infectedSitesListIndices) {
                susceptibleProbability[index] = 0.0;
                infectedProbability[index] = 1.0;
                diseasedProbability[index] = 0.0;
            }
        }
        public static double[] CalculateForceOfInfection(int landscapeX, int landscapeSize, double[] SHIM) {
            int timeStep = parameters.Timestep;
            double diseaseProgressionRatePerUnitTime = 0.02;
            double[] FOI = new double[landscapeSize];
            double[] susceptibleProbabilityNew = new double[landscapeSize];
            double[] infectedProbabilityNew = new double[landscapeSize];
            double[] diseasedProbabilityNew = new double[landscapeSize];
            for (int i = 0; i < landscapeSize; i++) {
                double sum = 0.0;
                (int x, int y) targetCoordinates = CalculateIndexToCoordinates(i, landscapeX);
                for (int j = 0; j < landscapeSize; j++) {
                    if (i == j) continue;
                    (int x, int y) sourceCoordinates = CalculateIndexToCoordinates(j, landscapeX);
                    (int x, int y) relativeCoordinates = CalculateRelativeGridOffset(targetCoordinates.x, targetCoordinates.y, sourceCoordinates.x, sourceCoordinates.y);
                    (int x, int y) canonicalizedRelativeCoordinates = CanonicalizeToHalfQuadrant(relativeCoordinates.x, relativeCoordinates.y);
                    double decay = GetDispersalProbability(CalculateCoordinatesToIndex(canonicalizedRelativeCoordinates.x, canonicalizedRelativeCoordinates.y, dispersalProbabilityMatrixWidth));
                    sum += SHIM[i]
                        * SHIM[j]
                        /* * Source Infection Probability Index */
                        * (infectedProbability[j] + diseasedProbability[j])
                        * decay;
                }
                FOI[i] = 
                    /* Transmission & Weather Index * */
                    sum;
                susceptibleProbabilityNew[i] = susceptibleProbability[i] - (FOI[i] * susceptibleProbability[i] * timeStep);
                infectedProbabilityNew[i] = infectedProbability[i] + ((FOI[i] * susceptibleProbability[i] * timeStep) - (diseaseProgressionRatePerUnitTime * (infectedProbability[i] * timeStep)));
                diseasedProbabilityNew[i] = diseasedProbability[i] + (diseaseProgressionRatePerUnitTime * infectedProbability[i] * timeStep);
            }
            susceptibleProbability = susceptibleProbabilityNew;
            infectedProbability = infectedProbabilityNew;
            diseasedProbability = diseasedProbabilityNew;
            return FOI;
        }
        public static double CalculateSiteHostIndexMax(ActiveSite site) {
            double maxScore = 0.0;
            foreach (ISpeciesCohorts speciesCohorts in universalCohorts[site]) {
                if (!speciesHostIndex.TryGetValue(speciesCohorts.Species, out HostIndex hostIndex)) {
                    continue;
                }
                ushort oldest = Util.GetMaxAge(universalCohorts[site][speciesCohorts.Species]);
                if (oldest == 0) continue;
                int hostCompetency = CalculateHostIndex(hostIndex, oldest);
                if (hostCompetency > maxScore) maxScore = hostCompetency;
            }
            return maxScore;
        }

        public static double CalculateSiteHostIndex(ActiveSite site) {
            switch (siteHostIndexMode) {
                case SHIMode.Mean:
                    return CalculateSiteHostIndexMean(site);
                case SHIMode.Max:
                    return CalculateSiteHostIndexMax(site);
                default:
                    throw new Exception($"Invalid SHI mode: {siteHostIndexMode}");
            }
        }

        

        //If given non-half-quadrate-canonicalized index, it will crash
        public static double GetDispersalProbability(int index) {
            return indexOffsetDispersalProbability[index];
        }

        

        private static double[] GenerateDispersalProbabilityMatrix(DispersalProbabilityKernel dispersalKernel, double alphaCoefficient, float cellLength, int maximumDispersalCellDistance, int maximumDispersalDistance) {
            double totalProbability = 0.0;
            Debug.Assert(cellLength > 0);
            //float cellArea = cellLength * cellLength;
            int dispersalProbabilityMatrixLength = dispersalProbabilityMatrixWidth * dispersalProbabilityMatrixHeight;
            Console.WriteLine($"Max radius: {dispersalProbabilityMatrixWidth}");
            double[] dispersalLookupMatrix = new double[dispersalProbabilityMatrixLength];
            int dispersalLookupMatrixCount = 0;
            for (int x = 0; x < dispersalProbabilityMatrixWidth; x++) {
                int yMax = Math.Min(x, dispersalProbabilityMatrixHeight - 1);
                for (int y = 0; y <= yMax; y++) {
                    if (x == 0 && y == 0) continue;
                    double distance = CalculatedEuclideanDistanceUsingGridOffset(x, y) * cellLength;
                    //Console.WriteLine($"x: {x}, y: {y}, Distance: {distance}");
                    //if (distance > maximumDispersalDistance && distance < maximumDispersalDistance + 20) Console.WriteLine($"Distance {distance}");
                    if (distance > maximumDispersalDistance) continue;
                    double probability = CalculateUncorrectedDispersalKernelProbability(dispersalKernel, distance, alphaCoefficient);
                    dispersalLookupMatrix[CalculateCoordinatesToIndex(x, y, dispersalProbabilityMatrixWidth)] = probability;
                    dispersalLookupMatrixCount++;
                    if (x == y || x == 0 || y == 0) {
                        totalProbability += probability * 4;
                    } else {
                        totalProbability += probability * 8;
                    }
                }
            };
            //normalize
            for (int x = 0; x < dispersalProbabilityMatrixLength; x++) {
                dispersalLookupMatrix[x] /= totalProbability;
            }
            Console.WriteLine($"Generated dispersal matrix with {dispersalLookupMatrixCount} entries");
            
            return dispersalLookupMatrix;
        }
        private static double CalculateUncorrectedDispersalKernelProbability(DispersalProbabilityKernel dispersalKernel, double distance, double alphaCoefficient) {
            switch(dispersalKernel) {
                case DispersalProbabilityKernel.PowerLaw:
                    return 1 / Math.Pow(distance, alphaCoefficient);
                case DispersalProbabilityKernel.NegativeExponent:
                    return Math.Exp(-alphaCoefficient * distance);
                case DispersalProbabilityKernel.SingleAnchoredPowerLaw:
                    return Math.Pow(PlugIn.ModelCore.CellLength / distance, 2);
                case DispersalProbabilityKernel.DoubleAnchoredPowerLaw:
                    //TODO: Move k math to program start, allow all parameters to be pulled from config
                    //      Even allowing the calibration points to go outside the bounds of the hard
                    //      dispersal cutoff and starting point to allow clipping for the shape
                    //worstCaseMaximumDispersalCellDistance.x is max dispersal distance / cell length
                    double k = Math.Log(1.0/0.01) / Math.Log(worstCaseMaximumDispersalCellDistance.x);
                    double a = 1.0 * Math.Pow(PlugIn.ModelCore.CellLength, k);
                    return a * Math.Pow(distance, -k);
                default:
                    throw new ArgumentException($"Dispersal type {dispersalKernel} not supported");
            }
        }
        /* private static double CalculateDispersalProbability(DispersalProbabilityKernel dispersalKernel, double distance, double alphaCoefficient, float cellLength, float cellArea) {
            if (distance == 0.0) {
                return 0.0;
            }
            double density;
            switch(dispersalKernel) {
                case DispersalProbabilityKernel.PowerLaw:
                    if (alphaCoefficient <= 0.0) return 0.0;
                    density = (alphaCoefficient * alphaCoefficient) / (2.0 * Math.PI) * Math.Pow(distance, -alphaCoefficient);
                    break;
                case DispersalProbabilityKernel.NegativeExponent:
                    double softeningLength = 0.5 * cellLength;
                    double normalization = ((alphaCoefficient - 1.0) * (alphaCoefficient - 2.0)) / (2.0 * Math.PI * softeningLength * softeningLength);
                    density = normalization * Math.Exp(-alphaCoefficient * distance / softeningLength);
                    break;
                case DispersalProbabilityKernel.InverseSquare:
                    density = Math.Pow(PlugIn.ModelCore.CellLength / distance, 2);
                    break;
                default:
                    throw new ArgumentException($"Dispersal type {dispersalKernel} not supported");
            }
            double probability = density * cellArea;
            if (probability < 0.0) probability = 0.0;
            if (probability > 1.0) probability = 1.0;
            return probability;
        } */

        public static ISiteVar<SiteCohorts> Cohorts
        {
            get
            {
                return universalCohorts;
            }
            set
            {
                universalCohorts = value;
            }
        }

        private static void GenerateProbabilityMatrixImage(double[] dispersalLookupMatrix) {
            int cellSize = 120;
            while (cellSize * dispersalProbabilityMatrixWidth > MAX_IMAGE_SIZE || cellSize * dispersalProbabilityMatrixHeight > MAX_IMAGE_SIZE) {
                cellSize--;
            }
            int imageWidth = dispersalProbabilityMatrixWidth * cellSize;
            int imageHeight = dispersalProbabilityMatrixHeight * cellSize;
            if (imageWidth <= 0 || imageHeight <= 0 || imageWidth > MAX_IMAGE_SIZE || imageHeight > MAX_IMAGE_SIZE) {
                Console.WriteLine($"Skipping image generation - invalid dimensions: {imageWidth}x{imageHeight}");
                return;
            }
            Console.WriteLine($"Image dimensions: {imageWidth}x{imageHeight}");
            Console.WriteLine($"Matrix dimensions: {dispersalProbabilityMatrixWidth}x{dispersalProbabilityMatrixHeight}");
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                using (Font font = new Font("Arial", 12))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (Pen gridPen = new Pen(Color.DimGray, 1))
                {
                    StringFormat stringFormat = new StringFormat();
                    stringFormat.Alignment = StringAlignment.Center;
                    stringFormat.LineAlignment = StringAlignment.Center;
                    for (int gridY = 0; gridY < dispersalProbabilityMatrixHeight; gridY++) {
                        for (int gridX = 0; gridX < dispersalProbabilityMatrixWidth; gridX++) {
                            double probability = dispersalLookupMatrix[CalculateCoordinatesToIndex(gridX, gridY, dispersalProbabilityMatrixWidth)];
                            int pixelX = gridX * cellSize;
                            int pixelY = gridY * cellSize;
                            if (gridX == 0 && gridY == 0 || probability != 0.0) {
                                graphics.DrawRectangle(gridPen, pixelX, pixelY, cellSize, cellSize);
                            }
                            if (probability == 0.0) continue;
                            string probabilityText = probability.ToString("E3");
                            RectangleF cellRect = new RectangleF(pixelX, pixelY, cellSize, cellSize);
                            graphics.DrawString(probabilityText, font, textBrush, cellRect, stringFormat);
                        }
                    }
                }
                string filename = $"canonicalized_dispersal_probability_matrix_radius_{worstCaseMaximumDispersalCellDistance}.png";
                bitmap.Save(filename, ImageFormat.Png);
            }
        }

        public static void GenerateSHIStateBitmap(string outputPath, double[] SHI) {
            byte scaleFactor = 120;
            while (scaleFactor * landscapeDimensions.x > MAX_IMAGE_SIZE || scaleFactor * landscapeDimensions.y > MAX_IMAGE_SIZE) {
                scaleFactor--;
            }
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                using (Font font = new Font("Arial", 12))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (Pen gridPen = new Pen(Color.DimGray, 1))
                {
                    StringFormat stringFormat = new StringFormat();
                    stringFormat.Alignment = StringAlignment.Center;
                    stringFormat.LineAlignment = StringAlignment.Center;
                    for (int gridY = 0; gridY < landscapeDimensions.y; gridY++) {
                        for (int gridX = 0; gridX < landscapeDimensions.x; gridX++) {
                            double value = SHI[CalculateCoordinatesToIndex(gridX, gridY, landscapeDimensions.x)];
                            int pixelX = gridX * scaleFactor;
                            int pixelY = gridY * scaleFactor;
                            graphics.DrawRectangle(gridPen, pixelX, pixelY, scaleFactor, scaleFactor);
                            string text = value.ToString(/* "E2" */);
                            RectangleF cellRect = new RectangleF(pixelX, pixelY, scaleFactor, scaleFactor);
                            graphics.DrawString(text, font, textBrush, cellRect, stringFormat);
                        }
                    }
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }
        public static void GenerateInfectionStateBitmap(string outputPath, List<(int x, int y)> healthySitesList, List<(int x, int y)> infectedSitesList, List<(int x, int y)> ignoredSitesList) {
            byte scaleFactor = 10;
            while (scaleFactor * landscapeDimensions.x > MAX_IMAGE_SIZE || scaleFactor * landscapeDimensions.y > MAX_IMAGE_SIZE) {
                scaleFactor--;
            }
            if (scaleFactor <= 0) {
                Console.WriteLine($"Skipping infection image generation - site too large to generate image");
                return;
            }
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            Bitmap bitmap = new Bitmap(imageWidth, imageHeight, PixelFormat.Format32bppArgb);
            
            Color healthyColor = Color.Green;
            Color infectedColor = Color.Red;
            Color ignoredColor = Color.Blue;
            foreach ((int x, int y) in healthySitesList) {
                int actualX = (x - 1) * scaleFactor;
                int actualY = (y - 1) * scaleFactor;
                for (int i = 0; i < scaleFactor; i++) {
                    for (int j = 0; j < scaleFactor; j++) {
                        int pixelX = actualX + i;
                        int pixelY = actualY + j;
                        bitmap.SetPixel(pixelX, pixelY, healthyColor);
                    }
                }
            }
            foreach ((int x, int y) in infectedSitesList) {
                int actualX = (x - 1) * scaleFactor;
                int actualY = (y - 1) * scaleFactor;
                for (int i = 0; i < scaleFactor; i++) {
                    for (int j = 0; j < scaleFactor; j++) {
                        int pixelX = actualX + i;
                        int pixelY = actualY + j;
                        bitmap.SetPixel(pixelX, pixelY, infectedColor);
                    }
                }
            }
            foreach ((int x, int y) in ignoredSitesList) {
                int actualX = (x - 1) * scaleFactor;
                int actualY = (y - 1) * scaleFactor;
                for (int i = 0; i < scaleFactor; i++) {
                    for (int j = 0; j < scaleFactor; j++) {
                        int pixelX = actualX + i;
                        int pixelY = actualY + j;
                        bitmap.SetPixel(pixelX, pixelY, ignoredColor);
                    }
                }
            }
            
            bitmap.Save(outputPath, ImageFormat.Png);
        }
    }
}
