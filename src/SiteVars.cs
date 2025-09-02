using Landis.Core;
using Landis.SpatialModeling;
using Landis.Library.UniversalCohorts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace Landis.Extension.Disturbance.DiseaseProgression
{    public static class SiteVars
    {
        private static ISiteVar<SiteCohorts> universalCohorts;
        private static double[,] indexOffsetDispersalProbability;
        private static (int x, int y) landscapeDimensions;
        private static int[,] resproutLifetime;
        private static (int x, int y) worstCaseMaximumDispersalCellDistance;
        //NOTE: no longer necessary to exist
        private static List<(int x, int y)> precalculatedDispersalDistanceOffsets;
        //TODO: Add to input parameters
        private static int resproutMaxLongevity;
        //TODO: Add to input parameters
        private static int resproutHalfLife;
        private const int MAX_IMAGE_SIZE = 16384;
        public static void Initialize(ICore modelCore, IInputParameters parameters) {
            universalCohorts = PlugIn.ModelCore.GetSiteVar<SiteCohorts>("Succession.UniversalCohorts");
            landscapeDimensions = (PlugIn.ModelCore.Landscape.Dimensions.Columns, PlugIn.ModelCore.Landscape.Dimensions.Rows);
            int worstCaseMaximumDispersalCellDistanceX = (int)Math.Ceiling(parameters.DispersalMaxDistance / PlugIn.ModelCore.CellLength);
            worstCaseMaximumDispersalCellDistance = (worstCaseMaximumDispersalCellDistanceX, (int)(worstCaseMaximumDispersalCellDistanceX * 0.7071067812) + 1);
            
            precalculatedDispersalDistanceOffsets = new List<(int x, int y)>();
            for (int x = -worstCaseMaximumDispersalCellDistance.x; x <= worstCaseMaximumDispersalCellDistance.x; x++) {
                for (int y = -worstCaseMaximumDispersalCellDistance.y; y <= worstCaseMaximumDispersalCellDistance.y; y++) {
                    if ((x == 0 && y == 0) || CalculateEuclideanDistance(x, y, 0, 0) > worstCaseMaximumDispersalCellDistance.x) continue;
                    precalculatedDispersalDistanceOffsets.Add((x, y));
                }
            };
            PlugIn.ModelCore.UI.WriteLine($"Generating dispersal lookup matrix for {LandscapeDimensions.x}x{LandscapeDimensions.y} landscape");
            indexOffsetDispersalProbability = GenerateDispersalLookupMatrix(parameters.DispersalProbabilityAlgorithm, parameters.AlphaCoefficient, PlugIn.ModelCore.CellLength, worstCaseMaximumDispersalCellDistance.x, parameters.DispersalMaxDistance);
            PlugIn.ModelCore.UI.WriteLine("Generating dispersal probability matrix image");
            GenerateProbabilityMatrixImage(indexOffsetDispersalProbability);
            //TODO: Initializes empty for now, but realistically the spinup cycle should add some sites to this
            resproutLifetime = new int[LandscapeDimensions.x, LandscapeDimensions.y];
            //TODO: Add to input parameters
            resproutMaxLongevity = 5/* parameters.ResproutMaxLongevity */;
            //TODO: Add to input parameters
            resproutHalfLife = 2/* parameters.resproutHalfLife */;
            PlugIn.ModelCore.UI.WriteLine($"Finished generating dispersal lookup matrix for {LandscapeDimensions.x}x{LandscapeDimensions.y} landscape");
        }
        public static int[,] ResproutLifetime {
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
                    if (resproutLifetime[x, y] > 0) resproutLifetime[x, y]--;
                }
            }
        }
        public static void AddResproutLifetime(int x, int y) {
            //TODO: This is a placeholder, determine a better way to implement lifetime
            int lifetime = resproutMaxLongevity;
            resproutLifetime[x, y] = Math.Min(resproutLifetime[x, y] + lifetime, resproutMaxLongevity);    
        }

        public static (int x, int y) CalculateRelativeGridOffset(int x1, int y1, int x2, int y2) {
            return (x2 - x1, y2 - y1);
        }

        private static double CalculateEuclideanDistance(int x1, int y1, int x2, int y2) {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }

        //If given non-half-quadrate-canonicalized value, it will crash
        public static double GetDispersalProbability(int canonicalized_x, int canonicalized_y) {
            return indexOffsetDispersalProbability[canonicalized_x, canonicalized_y];
        }

        //Reduces memory usage of the dictionary by 87.5%
        public static (int x, int y) CanonicalizeToHalfQuadrant(int x, int y) {
            int sx = x < 0 ? 1 : 0;
            int sy = y < 0 ? 1 : 0;
            int k = ((sy - sx) + 2 * (sx & sy)) & 3;
            (int x, int y) q1;
            switch (k) {
                case 0: q1 = (x, y); break;
                case 1: q1 = (-y, x); break;
                case 2: q1 = (-x, -y); break;
                default: q1 = (y, -x); break;
            }
            if (q1.y > q1.x) return (q1.y, q1.x);
            return q1;
        }

        private static double[,] GenerateDispersalLookupMatrix(DispersalProbabilityAlgorithm dispersalType, double alphaCoefficient, float cellLength, int maximumDispersalCellDistance, int maximumDispersalDistance) {
            Debug.Assert(cellLength > 0);
            float cellArea = cellLength * cellLength;
            int maxRadius = maximumDispersalCellDistance;
            Console.WriteLine($"Max radius: {maxRadius}");
            int maxY = (int)(maxRadius * 0.7071067812);
            double[,] dispersalLookupMatrix = new double[maxRadius, maxY];
            int dispersalLookupMatrixCount = 0;
            for (int x = 0; x < maxRadius; x++) {
                for (int y = 0; y < Math.Min(x, maxY); y++) {
                    if (x == 0 && y == 0) continue;
                    double distance = CalculateEuclideanDistance(x, y, 0, 0) * cellLength;
                    //Console.WriteLine($"x: {x}, y: {y}, Distance: {distance}");
                    //if (distance > maximumDispersalDistance) Console.WriteLine($"Distance {distance}");
                    if (distance > maximumDispersalDistance) continue;
                    double probability = CalculateDispersalProbability(dispersalType, distance, alphaCoefficient, cellLength, cellArea);
                    dispersalLookupMatrix[x, y] = probability;
                    dispersalLookupMatrixCount++;
                }
            };
            
            Console.WriteLine($"Generated dispersal matrix with {dispersalLookupMatrixCount} entries");
            
            return dispersalLookupMatrix;
        }

        private static void GenerateProbabilityMatrixImage(double[,] dispersalLookupMatrix) {
            int cellSize = 120;
            while (cellSize * (worstCaseMaximumDispersalCellDistance.x + 1) > MAX_IMAGE_SIZE) {
                cellSize--;
            }
            
            int matrixWidth = Math.Min(worstCaseMaximumDispersalCellDistance.x, MAX_IMAGE_SIZE / cellSize);
            int matrixHeight = (int)(matrixWidth * 0.7071067812);
            GenerateMatrixImage(dispersalLookupMatrix, matrixWidth, matrixHeight, cellSize);
        }
        
        private static void GenerateMatrixImage(double[,] dispersalLookupMatrix, int matrixWidth, int matrixHeight, int cellSize) {
            int imageWidth = matrixWidth * cellSize;
            //0.7071067812 is 1/1.4142135624 being half of pythagoras' constant
            int imageHeight = (int)(imageWidth * 0.7071067812);
            
            if (imageWidth <= 0 || imageHeight <= 0 || imageWidth > MAX_IMAGE_SIZE || imageHeight > MAX_IMAGE_SIZE) {
                Console.WriteLine($"Skipping image generation - invalid dimensions: {imageWidth}x{imageHeight}");
                return;
            }
            
            Console.WriteLine($"Image dimensions: {imageWidth}x{imageHeight}");
            Console.WriteLine($"Matrix dimensions: {matrixWidth}x{matrixHeight}");
            
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                
                using (Font font = new Font("Arial", 12))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    for (int gridY = 0; gridY < matrixHeight; gridY++) {
                        for (int gridX = 0; gridX < matrixWidth; gridX++) {
                            //Console.WriteLine($"gridX: {gridX}, gridY: {gridY}");
                            double probability = dispersalLookupMatrix[gridX, gridY];
                            //Console.WriteLine($"probability: {probability}");
                            if (probability == 0.0) continue;
                            int pixelX = gridX * cellSize;
                            int pixelY = gridY * cellSize;
                            
                            string probabilityText = probability.ToString("E2");
                            /* if (probability == 0.0) {
                                probabilityText = "0";
                            } else if (probability < 0.00000001) {
                                probabilityText = "~0";
                            } else {
                                probabilityText = probability.ToString("E2");
                            } */
                            
                            SizeF textSize = graphics.MeasureString(probabilityText, font);
                            float textX = pixelX + (cellSize - textSize.Width) / 2;
                            float textY = pixelY + (cellSize - textSize.Height) / 2;
                            
                            graphics.DrawString(probabilityText, font, textBrush, textX, textY);
                        }
                    }
                }
                
                string filename = $"canonicalized_dispersal_probability_matrix_radius_{worstCaseMaximumDispersalCellDistance}.png";
                bitmap.Save(filename, ImageFormat.Png);
            }
        }

        private static double CalculateDispersalProbability(DispersalProbabilityAlgorithm dispersalType, double distance, double alphaCoefficient, float cellLength, float cellArea) {
            if (distance == 0.0) {
                return 0.0;
            }
            double density;
            switch(dispersalType) {
                case DispersalProbabilityAlgorithm.PowerLaw:
                    if (alphaCoefficient <= 0.0) return 0.0;
                    density = (alphaCoefficient * alphaCoefficient) / (2.0 * Math.PI) * Math.Exp(-alphaCoefficient * distance);
                    break;
                case DispersalProbabilityAlgorithm.NegativeExponent:
                    double softeningLength = 0.5 * cellLength;
                    double normalization = ((alphaCoefficient - 1.0) * (alphaCoefficient - 2.0)) / (2.0 * Math.PI * softeningLength * softeningLength);
                    density = normalization * Math.Pow(1.0 + distance / softeningLength, -alphaCoefficient);
                    break;
                default:
                    throw new ArgumentException($"Dispersal type {dispersalType} not supported");
            }
            double probability = density * cellArea;
            if (probability < 0.0) probability = 0.0;
            if (probability > 1.0) probability = 1.0;
            return probability;
        }

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
        /* public static double[,] IndexOffsetDispersalProbabilityDictionary
        {
            get
            {
                return indexOffsetDispersalProbabilityDictionary;
            }
        } */
    }
}
