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
using System.Threading.Tasks;
using log4net.Core;
using System.Dynamic;
using System.Runtime.InteropServices;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public static class SiteVars
    {
        private static ISiteVar<SiteCohorts> universalCohorts;
        private static Dictionary<ISpecies, HostIndex> speciesHostIndex;
        private static int distanceDispersalDecayMatrixWidth;
        private static int distanceDispersalDecayMatrixHeight;
        private static double[] indexOffsetDistanceDispersalDecayMatrix;
        private static (int x, int y) landscapeDimensions;
        private static Dictionary<(int siteIndex, ISpecies species), ushort> resproutRemaining;
        private static (int x, int y) worstCaseMaximumDispersalCellDistance;
        private static (int x, int y)[] precomputedDispersalDistanceOffsets;
        private static int[] activeSiteIndices;
        private static HashSet<int> activeSiteIndicesSet;
        private static (int x, int y)[] precomputedLandscapeCoordinates;
        private static double[] normalizedWeatherIndex;
        private static double transmissionRate;
        //TODO: Add to input parameters
        private static int resproutMaxLongevity;
        private static double[] susceptibleProbability;
        private static double[] infectedProbability;
        private static double[] diseasedProbability;
        private static IInputParameters parameters;
        private const int MAX_IMAGE_SIZE = 16384;
        private static SHIMode siteHostIndexMode;
        private static bool[] wasInfectedLastTimestep;
        private static bool[] initialInfectionMap;
        public static void Initialize(ICore modelCore, IInputParameters inputParameters) {
            parameters = inputParameters;
            universalCohorts = PlugIn.ModelCore.GetSiteVar<SiteCohorts>("Succession.UniversalCohorts");
            landscapeDimensions = (PlugIn.ModelCore.Landscape.Dimensions.Columns, PlugIn.ModelCore.Landscape.Dimensions.Rows);
            speciesHostIndex = parameters.SpeciesHostIndex;
            susceptibleProbability = new double[landscapeDimensions.x * landscapeDimensions.y];
            infectedProbability = new double[landscapeDimensions.x * landscapeDimensions.y];
            diseasedProbability = new double[landscapeDimensions.x * landscapeDimensions.y];

            int worstCaseMaximumDispersalCellDistanceX = (int)Math.Ceiling(parameters.DispersalMaxDistance / PlugIn.ModelCore.CellLength);
            worstCaseMaximumDispersalCellDistance = (worstCaseMaximumDispersalCellDistanceX, (int)(worstCaseMaximumDispersalCellDistanceX * 0.7071067812) + 1);
            {
                List<(int x, int y)> precalculatedDispersalDistanceOffsetsList = new List<(int x, int y)>();
                for (int y = -worstCaseMaximumDispersalCellDistance.y; y <= worstCaseMaximumDispersalCellDistance.y; y++) {
                    for (int x = -worstCaseMaximumDispersalCellDistance.x; x <= worstCaseMaximumDispersalCellDistance.x; x++) {
                        if (Math.Abs(x) >= landscapeDimensions.x || Math.Abs(y) >= landscapeDimensions.y) continue;
                        if ((x == 0 && y == 0) || CalculatedEuclideanDistanceUsingGridOffset(x, y) > worstCaseMaximumDispersalCellDistance.x) continue;
                        precalculatedDispersalDistanceOffsetsList.Add((x, y));
                    }
                };
                precomputedDispersalDistanceOffsets = precalculatedDispersalDistanceOffsetsList.ToArray();
                //PlugIn.ModelCore.UI.WriteLine(string.Join(", ", precalculatedDispersalDistanceOffsets.Select(o => $"({o.x}, {o.y})")));
            }
            Log.Info(LogType.General, $"Generating dispersal lookup matrix for {LandscapeDimensions.x}x{LandscapeDimensions.y} landscape");
            distanceDispersalDecayMatrixWidth = worstCaseMaximumDispersalCellDistance.x + 1;
            distanceDispersalDecayMatrixHeight = (int)(worstCaseMaximumDispersalCellDistance.x * 0.7071067812) + 1;
            indexOffsetDistanceDispersalDecayMatrix = GenerateDistanceDispersalDecayMatrix(parameters.DistanceDispersalDecayKernelFunction, PlugIn.ModelCore.CellLength, worstCaseMaximumDispersalCellDistance.x, parameters.DispersalMaxDistance);
            Log.Info(LogType.General, "Generating dispersal probability matrix image");
            GenerateDistanceDispersalDecayMatrixImage(indexOffsetDistanceDispersalDecayMatrix);
            resproutRemaining = new Dictionary<(int siteIndex, ISpecies species), ushort>();
            //TODO: Add to input parameters
            resproutMaxLongevity = 5/* parameters.ResproutMaxLongevity */;
            //TODO: Add to input parameters
            IEnumerable<ActiveSite> sites = PlugIn.ModelCore.Landscape.ActiveSites;
            SHIMode = parameters.SHIMode;
            transmissionRate = parameters.TransmissionRate;
            Log.Info(LogType.General, $"Finished generating dispersal lookup matrix for {LandscapeDimensions.x}x{LandscapeDimensions.y} landscape");
            {
                //I needed a default that will make the program shit itself in some way if ever used
                (int x, int y) UNSET = (x: int.MinValue, y: int.MinValue);
                List<int> activeSiteList = new List<int>();
                (int x, int y)[] landscapeCoordinates = new (int x, int y)[landscapeDimensions.x * landscapeDimensions.y];
                for (int i = 0; i < landscapeDimensions.x * landscapeDimensions.y; i++) {
                    landscapeCoordinates[i] = UNSET;
                }
                foreach (Site site in sites) {
                    int index = CalculateCoordinatesToIndex(site.Location.Column - 1, site.Location.Row - 1, LandscapeDimensions.x);
                    activeSiteList.Add(index);
                    landscapeCoordinates[index] = (site.Location.Column - 1, site.Location.Row - 1);
                }
                activeSiteIndices = activeSiteList.ToArray();
                activeSiteIndicesSet = new HashSet<int>(activeSiteIndices);
                precomputedLandscapeCoordinates = landscapeCoordinates;
            }
            //weather index
            {
                double[] normalizedWeatherIndex_ = new double[landscapeDimensions.x * landscapeDimensions.y];
                var it = ((IEnumerable<int>)ActiveSiteIndices).GetEnumerator();
                bool hasNext = it.MoveNext();
                foreach (ActiveSite site in sites) {
                    Location location = site.Location;
                    int index = CalculateCoordinatesToIndex(location.Column - 1, location.Row - 1, landscapeDimensions.x);
                    while (hasNext && it.Current < index) hasNext = it.MoveNext();
                    if (hasNext && it.Current == index) {
                        /* double weatherIndex = SiteVars.ClimateVars[site]["AnnualWeatherIndex"];
                        double normalizedWI = weatherIndex / agent.EcoWeatherIndexNormal[PlugIn.ModelCore.Ecoregion[site].Index];
                        normalizedWeatherIndex_[index] = normalizedWI; */
                        hasNext = it.MoveNext();
                    }
                }
                normalizedWeatherIndex = normalizedWeatherIndex_;
            }
            initialInfectionMap = ReadInitialInfectionMap(modelCore, parameters.InitialInfectionPath, landscapeDimensions.x * landscapeDimensions.y);
        }
        public static bool[] InitialInfectionMap {
            get {
                return initialInfectionMap;
            }
        }

        public static double MinActive(double[] array) {
            double min = double.PositiveInfinity;
            foreach (int i in activeSiteIndices) {
                if (array[i] < min) min = array[i];
            }
            Trace.Assert(min != double.PositiveInfinity, "Min is positive infinity");
            return min;
        }
        public static double MaxActive(double[] array) {
            double max = double.NegativeInfinity;
            foreach (int i in activeSiteIndices) {
                if (array[i] > max) max = array[i];
            }
            Trace.Assert(max != double.NegativeInfinity, "Max is negative infinity");
            return max;
        }
        public static (double min, double max) MinMaxActive(double[] array) {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (int i in activeSiteIndices) {
                if (array[i] < min) min = array[i];
                if (array[i] > max) max = array[i];
            }
            Trace.Assert(min != double.PositiveInfinity, "Min is positive infinity");
            Trace.Assert(max != double.NegativeInfinity, "Max is negative infinity");
            return (min, max);
        }
        public static int[] ActiveSiteIndices {
            get {
                return activeSiteIndices;
            }
        }
        public static (int x, int y)[] PrecomputedLandscapeCoordinates {
            get {
                return precomputedLandscapeCoordinates;
            }
        }
        public static (int x, int y)[] PrecomputedDispersalDistanceOffsets {
            get {
                return precomputedDispersalDistanceOffsets;
            }
        }

        public static HashSet<int> ActiveSiteIndicesSet {
            get {
                return activeSiteIndicesSet;
            }
        }
        public static SHIMode SHIMode {
            get {
                return siteHostIndexMode;
            }
            set {
                siteHostIndexMode = value;
            }
        }
        public static int DistanceDispersalDecayMatrixWidth {
            get {
                return distanceDispersalDecayMatrixWidth;
            }
        }
        public static int DistanceDispersalDecayMatrixHeight {
            get {
                return distanceDispersalDecayMatrixHeight;
            }
        }
        public static Dictionary<(int siteIndex, ISpecies species), ushort> ResproutRemaining => resproutRemaining;

        /* public static int[] PrecalculatedDispersalDistanceOffsets {
            get {
                return precalculatedDispersalDistanceOffsets;
            }
        } */
        public static (int x, int y) LandscapeDimensions {
            get {
                return landscapeDimensions;
            }
        }

        public static (int x, int y) GetWorstCaseMaximumUniformDispersalDistance() {
            return worstCaseMaximumDispersalCellDistance;
        }
        public static void DecrementResproutLifetimes() {
            if (resproutRemaining.Count == 0) return;
            List<(int siteIndex, ISpecies species)> toRemove = null;
            foreach (var key in resproutRemaining.Keys.ToList()) {
                ushort v = resproutRemaining[key];
                if (v > 0) v--;
                if (v == 0) {
                    if (toRemove == null) toRemove = new List<(int siteIndex, ISpecies species)>();
                    toRemove.Add(key);
                } else {
                    resproutRemaining[key] = v;
                }
            }
            if (toRemove != null) {
                foreach (var k in toRemove) resproutRemaining.Remove(k);
            }
        }
        public static void AddResproutLifetime(int siteIndex, ISpecies species) {
            ISpecies healthySpecies = parameters.GetDesignatedHealthySpecies(species);
            if (healthySpecies == null) throw new InvalidOperationException($"Designated healthy species not found for {species.Name}.");
            ushort add = (ushort)Math.Min(resproutMaxLongevity, ushort.MaxValue);
            ushort current = 0;
            if (resproutRemaining.TryGetValue((siteIndex, healthySpecies), out ushort existing)) current = existing;
            uint sum = (uint)current + add;
            ushort capped = (ushort)Math.Min(sum, (uint)resproutMaxLongevity);
            if (capped == 0) {
                resproutRemaining.Remove((siteIndex, healthySpecies));
            } else {
                resproutRemaining[(siteIndex, healthySpecies)] = capped;
            }
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
        public static void SetDefaultProbabilities(List<int> healthySitesListIndices, List<int> infectedSitesListIndices, List<int> ignoredSitesListIndices) {
            bool[] infected = new bool[landscapeDimensions.x * landscapeDimensions.y];
            foreach (int index in healthySitesListIndices) {
                susceptibleProbability[index] = 1.0;
                infectedProbability[index] = 0.0;
                diseasedProbability[index] = 0.0;
            }
            foreach (int index in infectedSitesListIndices) {
                susceptibleProbability[index] = 0.0;
                infectedProbability[index] = 1.0;
                diseasedProbability[index] = 0.0;
                infected[index] = true;
            }
            foreach (int index in ignoredSitesListIndices) {
                susceptibleProbability[index] = 1.0;
                infectedProbability[index] = 0.0;
                diseasedProbability[index] = 0.0;
            }
            wasInfectedLastTimestep = infected;
        }

        public static void EnforceInfectedProbability(List<int> infectedSitesListIndices) {
            int landscapeSize = LandscapeDimensions.x * LandscapeDimensions.y;
            bool[] infected = new bool[landscapeSize];
            foreach (int index in infectedSitesListIndices) {
                infected[index] = true;
            }
            foreach (int i in activeSiteIndices) {
                if (wasInfectedLastTimestep[i] && !infected[i]) {
                    susceptibleProbability[i] = 1.0;
                    infectedProbability[i] = 0.0;
                    diseasedProbability[i] = 0.0;
                } else if (!wasInfectedLastTimestep[i] && infected[i]) {
                    susceptibleProbability[i] = 0.0;
                    infectedProbability[i] = 1.0;
                    diseasedProbability[i] = 0.0;
                }                
            }
            wasInfectedLastTimestep = infected;
        }
        public static double[] CalculateForceOfInfection(int landscapeSize, double[] SHIM) {
            int timeStep = parameters.Timestep;
            //TODO: Needs to be passed in by the config file
            double diseaseProgressionRatePerUnitTime = 0.0;
            
            double[] FOI = new double[landscapeSize];
            double[] susceptibleProbabilityNew = new double[landscapeSize];
            double[] infectedProbabilityNew = new double[landscapeSize];
            double[] diseasedProbabilityNew = new double[landscapeSize];
            Parallel.ForEach(activeSiteIndices, i => {
                double beta_t = normalizedWeatherIndex[i] * transmissionRate;
                double sum = 0.0;
                (int x, int y) targetCoordinates = precomputedLandscapeCoordinates[i];
                foreach ((int x, int y) in precomputedDispersalDistanceOffsets) {
                    if (targetCoordinates.x + x < 0
                        || targetCoordinates.x + x >= landscapeDimensions.x
                        || targetCoordinates.y + y < 0
                        || targetCoordinates.y + y >= landscapeDimensions.y
                    ) continue;
                    int index = CalculateCoordinatesToIndex(x, y, landscapeDimensions.x);
                    if (!activeSiteIndicesSet.Contains(index + i)) continue;
                    int j = index + i;
                    (int x, int y) canonicalizedRelativeCoordinates = CanonicalizeToHalfQuadrant(x, y);
                    if (canonicalizedRelativeCoordinates.x >= distanceDispersalDecayMatrixWidth || canonicalizedRelativeCoordinates.y >= distanceDispersalDecayMatrixHeight) continue;
                    double decay = GetDistanceDispersalDecay(CalculateCoordinatesToIndex(canonicalizedRelativeCoordinates.x, canonicalizedRelativeCoordinates.y, distanceDispersalDecayMatrixWidth));
                    sum += SHIM[i]
                        * SHIM[j]
                        * (infectedProbability[j] + diseasedProbability[j])
                        * decay;
                }
                FOI[i] = 
                    /* Transmission & Weather Index * */
                    sum;
                /* Trace.Assert(
                    susceptibleProbability[i] + infectedProbability[i] + diseasedProbability[i] == 1.0,
                    $"SusceptibleProbability: {susceptibleProbability[i]}, InfectedProbability: {infectedProbability[i]}, DiseasedProbability: {diseasedProbability[i]}, total: {susceptibleProbability[i] + infectedProbability[i] + diseasedProbability[i]}"
                ); */
                susceptibleProbabilityNew[i] = susceptibleProbability[i] - (FOI[i] * susceptibleProbability[i] * timeStep);
                infectedProbabilityNew[i] = infectedProbability[i] + ((FOI[i] * susceptibleProbability[i] * timeStep) - (diseaseProgressionRatePerUnitTime * (infectedProbability[i] * timeStep)));
                diseasedProbabilityNew[i] = diseasedProbability[i] + (diseaseProgressionRatePerUnitTime * infectedProbability[i] * timeStep);
            });
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
        public static double GetDistanceDispersalDecay(int index) {
            return indexOffsetDistanceDispersalDecayMatrix[index];
        }

        

        private static double[] GenerateDistanceDispersalDecayMatrix(IDistanceDispersalDecayKernel kernel, float cellLength, int maximumDispersalCellDistance, int maximumDispersalDistance) {
            double totalProbability = 0.0;
            Debug.Assert(cellLength > 0);
            //float cellArea = cellLength * cellLength;
            int dispersalProbabilityMatrixLength = distanceDispersalDecayMatrixWidth * distanceDispersalDecayMatrixHeight;
            Log.Info(LogType.General, $"Max radius: {distanceDispersalDecayMatrixWidth}");
            double[] dispersalLookupMatrix = new double[dispersalProbabilityMatrixLength];
            int dispersalLookupMatrixCount = 0;
            for (int x = 0; x < distanceDispersalDecayMatrixWidth; x++) {
                int yMax = Math.Min(x, distanceDispersalDecayMatrixHeight - 1);
                for (int y = 0; y <= yMax; y++) {
                    if (x == 0 && y == 0) continue;
                    double distance = CalculatedEuclideanDistanceUsingGridOffset(x, y) * cellLength;
                    //Console.WriteLine($"x: {x}, y: {y}, Distance: {distance}");
                    //if (distance > maximumDispersalDistance && distance < maximumDispersalDistance + 20) Console.WriteLine($"Distance {distance}");
                    if (distance > maximumDispersalDistance) continue;
                    double probability = kernel.Compute(distance);
                    dispersalLookupMatrix[CalculateCoordinatesToIndex(x, y, distanceDispersalDecayMatrixWidth)] = probability;
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
            Log.Info(LogType.General, $"Generated dispersal matrix with {dispersalLookupMatrixCount} entries");
            
            return dispersalLookupMatrix;
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

        private static void GenerateDistanceDispersalDecayMatrixImage(double[] dispersalLookupMatrix) {
            int cellSize = 120;
            while (cellSize * distanceDispersalDecayMatrixWidth > MAX_IMAGE_SIZE || cellSize * distanceDispersalDecayMatrixHeight > MAX_IMAGE_SIZE) {
                cellSize--;
            }
            int imageWidth = distanceDispersalDecayMatrixWidth * cellSize;
            int imageHeight = distanceDispersalDecayMatrixHeight * cellSize;
            if (imageWidth <= 0 || imageHeight <= 0 || imageWidth > MAX_IMAGE_SIZE || imageHeight > MAX_IMAGE_SIZE) {
                Log.Error(LogType.General, $"Skipping image generation - invalid dimensions: {imageWidth}x{imageHeight}");
                return;
            }
            Log.Info(LogType.General, $"Image dimensions: {imageWidth}x{imageHeight}");
            Log.Info(LogType.General, $"Matrix dimensions: {distanceDispersalDecayMatrixWidth}x{distanceDispersalDecayMatrixHeight}");
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
                    for (int gridY = 0; gridY < distanceDispersalDecayMatrixHeight; gridY++) {
                        for (int gridX = 0; gridX < distanceDispersalDecayMatrixWidth; gridX++) {
                            double probability = dispersalLookupMatrix[CalculateCoordinatesToIndex(gridX, gridY, distanceDispersalDecayMatrixWidth)];
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

        public static void SerializeAsBincode(string outputPath, int timestep, double[] data) {
            int width = landscapeDimensions.x;
            int height = landscapeDimensions.y;
            ulong count = (ulong)((long)width * (long)height);
            if (data == null) throw new ArgumentNullException(nameof(data));
            if ((ulong)data.LongLength != count) throw new ArgumentException("Data length does not match width*height.");
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(fs)) {
                writer.Write((uint)timestep);
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write(count);
                for (long i = 0; i < data.LongLength; i++) {
                    writer.Write(data[i]);
                }
            }
        }

        public static void SerializeAsBincode(string outputPath, int timestep, int[] data) {
            int width = landscapeDimensions.x;
            int height = landscapeDimensions.y;
            ulong count = (ulong)((long)width * (long)height);
            if (data == null) throw new ArgumentNullException(nameof(data));
            if ((ulong)data.LongLength != count) throw new ArgumentException("Data length does not match width*height.");
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(fs)) {
                writer.Write((uint)timestep);
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write(count);
                for (long i = 0; i < data.LongLength; i++) {
                    writer.Write(data[i]);
                }
            }
        }

        public static void SerializeAsBincode(string outputPath, int timestep, List<(int x, int y)> healthySitesList, List<(int x, int y)> infectedSitesList, List<(int x, int y)> ignoredSitesList) {
            int width = landscapeDimensions.x;
            int height = landscapeDimensions.y;
            if (healthySitesList == null) throw new ArgumentNullException(nameof(healthySitesList));
            if (infectedSitesList == null) throw new ArgumentNullException(nameof(infectedSitesList));
            if (ignoredSitesList == null) throw new ArgumentNullException(nameof(ignoredSitesList));
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write((uint)timestep);
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write((ulong)healthySitesList.Count);
                for (int i = 0; i < healthySitesList.Count; i++) {
                    int x = healthySitesList[i].x;
                    int y = healthySitesList[i].y;
                    if (x < 0 || y < 0) throw new ArgumentOutOfRangeException("Coordinates must be non-negative.");
                    writer.Write((uint)x);
                    writer.Write((uint)y);
                }
                writer.Write((ulong)infectedSitesList.Count);
                for (int i = 0; i < infectedSitesList.Count; i++) {
                    int x = infectedSitesList[i].x;
                    int y = infectedSitesList[i].y;
                    if (x < 0 || y < 0) throw new ArgumentOutOfRangeException("Coordinates must be non-negative.");
                    writer.Write((uint)x);
                    writer.Write((uint)y);
                }
                writer.Write((ulong)ignoredSitesList.Count);
                for (int i = 0; i < ignoredSitesList.Count; i++) {
                    int x = ignoredSitesList[i].x;
                    int y = ignoredSitesList[i].y;
                    if (x < 0 || y < 0) throw new ArgumentOutOfRangeException("Coordinates must be non-negative.");
                    writer.Write((uint)x);
                    writer.Write((uint)y);
                }
            }
        }

        public static void GenerateBitmap(string outputPath, byte[] data) {
            int width = landscapeDimensions.x;
            int height = landscapeDimensions.y;
            ulong count = (ulong)((long)width * (long)height);
            if (data == null) throw new ArgumentNullException(nameof(data));
            if ((ulong)data.LongLength != count) throw new ArgumentException("Data length does not match width*height.");
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(fs)) {
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write(count);
                for (long i = 0; i < data.LongLength; i++) {
                    writer.Write(data[i]);
                }
            }
        }

        public static void GenerateNumericalStateBitmap(string outputPath, double[] SHI) {
            byte scaleFactor = 120;
            while (scaleFactor * landscapeDimensions.x > MAX_IMAGE_SIZE || scaleFactor * landscapeDimensions.y > MAX_IMAGE_SIZE) {
                scaleFactor--;
            }
            float fontScaleFactor = 1 - ((1 - (scaleFactor / 120.0f)) / 2.0f);
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                using (Font font = new Font("Arial", fontScaleFactor * 12))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    using (Pen gridPen = new Pen(Color.DimGray, 1))
                    {
                        for (int gx = 1; gx < landscapeDimensions.x; gx++) {
                            int x = gx * scaleFactor;
                            graphics.DrawLine(gridPen, x, 0, x, imageHeight - 1);
                        }
                        for (int gy = 1; gy < landscapeDimensions.y; gy++) {
                            int y = gy * scaleFactor;
                            graphics.DrawLine(gridPen, 0, y, imageWidth - 1, y);
                        }
                        graphics.DrawRectangle(gridPen, 0, 0, imageWidth - 1, imageHeight - 1);
                    }
                    StringFormat stringFormat = new StringFormat();
                    stringFormat.Alignment = StringAlignment.Center;
                    stringFormat.LineAlignment = StringAlignment.Center;
                    for (int gridY = 0; gridY < landscapeDimensions.y; gridY++) {
                        for (int gridX = 0; gridX < landscapeDimensions.x; gridX++) {
                            double value = SHI[CalculateCoordinatesToIndex(gridX, gridY, landscapeDimensions.x)];
                            int pixelX = gridX * scaleFactor;
                            int pixelY = gridY * scaleFactor;
                            string text = DoubleFormatter(value);
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
                Log.Error(LogType.General, $"Skipping infection image generation - site too large to generate image");
                return;
            }
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight, PixelFormat.Format32bppArgb))
            {
                Rectangle rect = new Rectangle(0, 0, imageWidth, imageHeight);
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try {
                    int stride = data.Stride;
                    IntPtr scan0 = data.Scan0;

                    byte[] greenRow = new byte[scaleFactor * 4];
                    byte[] redRow = new byte[scaleFactor * 4];
                    byte[] blueRow = new byte[scaleFactor * 4];
                    for (int i = 0; i < scaleFactor; i++) {
                        int p = i * 4;
                        blueRow[p + 0] = 255; blueRow[p + 1] = 0;   blueRow[p + 2] = 0;   blueRow[p + 3] = 255;
                        greenRow[p + 0] = 0;   greenRow[p + 1] = 128; greenRow[p + 2] = 0;   greenRow[p + 3] = 255;
                        redRow[p + 0] = 0;   redRow[p + 1] = 0;   redRow[p + 2] = 255; redRow[p + 3] = 255;
                    }

                    void FillBlocks(List<(int x, int y)> coords, byte[] rowTemplate) {
                        foreach ((int x, int y) in coords) {
                            int actualX = (x - 1) * scaleFactor;
                            int actualY = (y - 1) * scaleFactor;
                            for (int jj = 0; jj < scaleFactor; jj++) {
                                IntPtr dest = scan0 + (actualY + jj) * stride + actualX * 4;
                                Marshal.Copy(rowTemplate, 0, dest, rowTemplate.Length);
                            }
                        }
                    }

                    FillBlocks(healthySitesList, greenRow);
                    FillBlocks(infectedSitesList, redRow);
                    FillBlocks(ignoredSitesList, blueRow);
                }
                finally {
                    bitmap.UnlockBits(data);
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }
        public static void GenerateIntensityBitmap(string outputPath, double[] intensities) {
            int landscapeSize = LandscapeDimensions.x * LandscapeDimensions.y;
            byte scaleFactor = 10;
            while (scaleFactor * landscapeDimensions.x > MAX_IMAGE_SIZE || scaleFactor * landscapeDimensions.y > MAX_IMAGE_SIZE) {
                scaleFactor--;
            }
            if (scaleFactor <= 0) {
                Log.Error(LogType.General, $"Skipping intensity image generation - site too large to generate image");
                return;
            }
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight, PixelFormat.Format32bppArgb))
            {
                (double min, double max) = MinMaxActive(intensities);
                bool useMinMax = !(double.IsInfinity(min) || double.IsInfinity(max) || max <= min);
                Rectangle rect = new Rectangle(0, 0, imageWidth, imageHeight);
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try {
                    int stride = data.Stride;
                    IntPtr scan0 = data.Scan0;
                    byte[] rowBuffer = new byte[scaleFactor * 4];

                    int width = landscapeDimensions.x;
                    for (int i = 0; i < landscapeSize; i++) {
                        (int x, int y) coordinates = CalculateIndexToCoordinates(i, width);
                        double v = intensities[i];
                        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;
                        double n = useMinMax ? (v - min) / (max - min) : v;
                        if (n < 0.0) n = 0.0; else if (n > 1.0) n = 1.0;
                        byte intensity = (byte)Math.Round(n * 255.0);
                        for (int k = 0; k < scaleFactor; k++) {
                            int p = k * 4;
                            rowBuffer[p + 0] = intensity;
                            rowBuffer[p + 1] = intensity;
                            rowBuffer[p + 2] = intensity;
                            rowBuffer[p + 3] = 255;
                        }
                        int actualX = coordinates.x * scaleFactor;
                        int actualY = coordinates.y * scaleFactor;
                        for (int jj = 0; jj < scaleFactor; jj++) {
                            IntPtr dest = scan0 + (actualY + jj) * stride + actualX * 4;
                            Marshal.Copy(rowBuffer, 0, dest, rowBuffer.Length);
                        }
                    }
                }
                finally {
                    bitmap.UnlockBits(data);
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }
        public static void GenerateMultiStateBitmap(string outputPath, Color[] colours) {
            byte scaleFactor = 120;
            while (scaleFactor * landscapeDimensions.x > MAX_IMAGE_SIZE || scaleFactor * landscapeDimensions.y > MAX_IMAGE_SIZE) {
                scaleFactor--;
            }
            if (scaleFactor <= 4) {
                Log.Error(LogType.General, $"Skipping state bitmap generation - site too large to generate image");
                return;
            }
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                using (SolidBrush brTL = new SolidBrush(Color.Black))
                using (SolidBrush brTR = new SolidBrush(Color.Black))
                using (SolidBrush brBL = new SolidBrush(Color.Black))
                using (SolidBrush brBR = new SolidBrush(Color.Black))
                {
                    for (int gridY = 0; gridY < landscapeDimensions.y; gridY++) {
                        for (int gridX = 0; gridX < landscapeDimensions.x; gridX++) {
                            int index = CalculateCoordinatesToIndex(gridX, gridY, landscapeDimensions.x);
                            if (index < 0 || index >= colours.Length) continue;
                            Color c = colours[index];
                            int pixelX = gridX * scaleFactor;
                            int pixelY = gridY * scaleFactor;
                            int halfW = scaleFactor / 2;
                            int halfH = scaleFactor / 2;
                            Rectangle tl = new Rectangle(pixelX, pixelY, halfW, halfH);
                            Rectangle tr = new Rectangle(pixelX + halfW, pixelY, scaleFactor - halfW, halfH);
                            Rectangle bl = new Rectangle(pixelX, pixelY + halfH, halfW, scaleFactor - halfH);
                            Rectangle br = new Rectangle(pixelX + halfW, pixelY + halfH, scaleFactor - halfW, scaleFactor - halfH);
                            brTL.Color = Color.FromArgb(255, c.R, 0, 0);
                            brTR.Color = Color.FromArgb(255, 0, c.G, 0);
                            brBL.Color = Color.FromArgb(255, 0, 0, c.B);
                            brBR.Color = Color.FromArgb(255, c.R, c.G, c.B);
                            graphics.FillRectangle(brTL, tl);
                            graphics.FillRectangle(brTR, tr);
                            graphics.FillRectangle(brBL, bl);
                            graphics.FillRectangle(brBR, br);
                        }
                    }
                }
                using (Pen gridPen = new Pen(Color.DimGray, 1))
                {
                    for (int gx = 1; gx < landscapeDimensions.x; gx++) {
                        int x = gx * scaleFactor;
                        graphics.DrawLine(gridPen, x, 0, x, imageHeight - 1);
                    }
                    for (int gy = 1; gy < landscapeDimensions.y; gy++) {
                        int y = gy * scaleFactor;
                        graphics.DrawLine(gridPen, 0, y, imageWidth - 1, y);
                    }
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }
        public static void GenerateOverallStateBitmap(string outputPath, Color[] colours) {
            byte scaleFactor = 120;
            while (scaleFactor * landscapeDimensions.x > MAX_IMAGE_SIZE || scaleFactor * landscapeDimensions.y > MAX_IMAGE_SIZE) {
                scaleFactor--;
            }
            if (scaleFactor <= 4) {
                Log.Error(LogType.General, $"Skipping state bitmap generation - site too large to generate image");
                return;
            }
            int imageWidth = landscapeDimensions.x * scaleFactor;
            int imageHeight = landscapeDimensions.y * scaleFactor;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                using (SolidBrush brush = new SolidBrush(Color.Black))
                {
                    for (int gridY = 0; gridY < landscapeDimensions.y; gridY++) {
                        for (int gridX = 0; gridX < landscapeDimensions.x; gridX++) {
                            int index = CalculateCoordinatesToIndex(gridX, gridY, landscapeDimensions.x);
                            if (index < 0 || index >= colours.Length) continue;
                            Color c = colours[index];
                            int pixelX = gridX * scaleFactor;
                            int pixelY = gridY * scaleFactor;
                            Rectangle cell = new Rectangle(pixelX, pixelY, scaleFactor, scaleFactor);
                            brush.Color = c;
                            graphics.FillRectangle(brush, cell);
                        }
                    }
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }
        
        public static bool[] ReadInitialInfectionMap(ICore modelCore, string path, int landscapeSize) {
            if (path == null) return null;
            bool[] initialInfectionMap = new bool[landscapeSize];
            IInputRaster<UIntPixel> map = modelCore.OpenRaster<UIntPixel>(path);
            
            using (map) {
                UIntPixel pixel = map.BufferPixel;
                foreach (Site site in modelCore.Landscape.AllSites) {
                    map.ReadBufferPixel();
                    uint mapValue = pixel.MapCode.Value;
                    
                    if (site.IsActive) {
                        if (mapValue == 1) {
                            Location location = site.Location;
                            int index = CalculateCoordinatesToIndex(location.Column - 1, location.Row - 1, LandscapeDimensions.x);
                            initialInfectionMap[index] = true;
                        }
                        /* targetSiteVar[site] = (mapValue == 1); */
                    }
                }
            }
            return initialInfectionMap;
        }

        public static void ProportionSites(IEnumerable<ActiveSite> sites, bool[] sitesForProportioning, int landscapeX, ExtensionType disturbanceType, int landscapeSize) {
            double[] mortalityTracker = new double[landscapeSize];
            Dictionary<ISpecies, Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>>();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                int index = CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX);
                if (!sitesForProportioning[index]) continue;
                SiteCohorts siteCohorts = Cohorts[site];
                
                foreach (ISpeciesCohorts speciesCohorts in siteCohorts) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    foreach (ICohort cohort in concreteSpeciesCohorts) {
                        Cohort concreteCohort = (Cohort)cohort;
                        ISpecies designatedHealthySpecies = parameters.GetDesignatedHealthySpecies(speciesCohorts.Species);

                        //process entry through matrix
                        (ISpecies, double)[] transitionDistribution = parameters.GetSpeciesTransitionAgeMatrixDistribution(speciesCohorts.Species, cohort.Data.Age);

                        //no transition will occur
                        if (transitionDistribution == null) {
                            if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                            }
                            if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                            }
                            (int biomass, Dictionary<string, int> additionalParameters) entry = newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age];
                            entry.biomass += concreteCohort.Data.Biomass;
                            foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                                if (!entry.additionalParameters.ContainsKey(parameter.Key)) {
                                    entry.additionalParameters[parameter.Key] = 0;
                                }
                                entry.additionalParameters[parameter.Key] += (int)parameter.Value;
                            }
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = entry;
                            continue; //short-circuit
                        }

                        
                        if (designatedHealthySpecies != null) {
                            Log.Info(LogType.General, $"designated healthy species: {designatedHealthySpecies.Name}");
                        }
                        Log.Info(LogType.General, $"species: {speciesCohorts.Species.Name}, age: {concreteCohort.Data.Age}, biomass: {concreteCohort.Data.Biomass}");

                        //exists to account for the error created during the cast from float to int
                        //with low biomass values, the error accumilation can be significant
                        //biomass being stored as an int is a design issue in landis-core which
                        //results in biomass not being kept in the live cohort or going to decomposition
                        //pools as a result of Math.Floor being used in truncation of positive float values
                        //to int it simply gets discarded
                        int totalBiomassAccountedFor = 0;
                        int remainingBiomass = concreteCohort.Data.Biomass;

                        Dictionary<string, int> remainingAdditionalParameters = new Dictionary<string, int>();
                        foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                            //Console.WriteLine($"1Parameter: {parameter.Key}, Value: {parameter.Value}");
                            remainingAdditionalParameters[parameter.Key] = (int)parameter.Value;
                        }
                        foreach ((ISpecies targetSpecies, double proportion) in transitionDistribution) {                            
                            if (remainingBiomass == 0) {
                                break;
                            }
                            //ModelCore.UI.WriteLine($"Before rounding: {concreteCohort.Data.Biomass * proportion}");
                            int transfer = (int)Math.Round(concreteCohort.Data.Biomass * proportion);
                            //ModelCore.UI.WriteLine($"Rounded: {Math.Round(concreteCohort.Data.Biomass * proportion)}");
                            //ModelCore.UI.WriteLine($"Cast: {transfer}");
                            if (remainingBiomass - transfer < 0) {
                                transfer = remainingBiomass;
                            }
                            remainingBiomass -= transfer;
                            totalBiomassAccountedFor += transfer;
                            //Console.WriteLine($"{(targetSpecies == null ? "DEAD" : targetSpecies.Name)}");
                            if (targetSpecies == null || concreteCohort.Data.Biomass == 1) {
                                //This is a hacky way to kill miniscule cohorts
                                if (concreteCohort.Data.Biomass == 1) {
                                    transfer = 1;
                                    remainingBiomass -= transfer;
                                    totalBiomassAccountedFor += transfer;
                                }
                                mortalityTracker[index] += (double)concreteCohort.Data.Biomass * proportion;
                                //Console.WriteLine($"Transitioning some biomass to dead: {(double)concreteCohort.Data.Biomass * proportion}, biomass of cohort: {concreteCohort.Data.Biomass}, proportion: {proportion}");
                                //TODO: Should I be feeding 1.0 for the proportion here so it kills the entire cohort in the case of where biomass == 1?
                                Cohort.CohortMortality(concreteSpeciesCohorts, concreteCohort, site, disturbanceType, (float)proportion);
                                Log.Info(LogType.Transitions, $"Transitioned to dead: Age: {concreteCohort.Data.Age}, Biomass: {concreteCohort.Data.Biomass}, Species: {speciesCohorts.Species.Name}");
                                AddResproutLifetime(CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX), designatedHealthySpecies);
                                continue; //short-circuit
                            }

                            //push biomass to target species cohort
                            if (!newSiteCohortsDictionary.ContainsKey(targetSpecies)) {
                                newSiteCohortsDictionary[targetSpecies] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                            }
                            if (!newSiteCohortsDictionary[targetSpecies].ContainsKey(concreteCohort.Data.Age)) {
                                newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                            }
                            (int biomass, Dictionary<string, int> additionalParameters) entry = newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age];
                            entry.biomass += transfer;
                            int originalBiomass = concreteCohort.Data.Biomass;
                            foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                                //Console.WriteLine($"P Parameter: {parameter.Key}, Value: {parameter.Value}");
                                if (!entry.additionalParameters.ContainsKey(parameter.Key)) {
                                    entry.additionalParameters[parameter.Key] = 0;
                                }
                                int available = remainingAdditionalParameters[parameter.Key];
                                int alloc = originalBiomass > 0 ? (int)(((long)available * transfer + (originalBiomass / 2)) / originalBiomass) : 0;
                                if (alloc < 0) alloc = 0;
                                if (alloc > available) alloc = available;
                                entry.additionalParameters[parameter.Key] += alloc;
                                remainingAdditionalParameters[parameter.Key] -= alloc;
                                Trace.Assert((int)remainingAdditionalParameters[parameter.Key] >= 0);
                            }
                            if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                            }
                            if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                            }
                            newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] = entry;
                            Log.Info(LogType.Transitions, $"Transferred {transfer} biomass from {speciesCohorts.Species.Name} to {targetSpecies.Name}");
                            
                        }
                        //push remaining biomass to original species cohort
                        if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                            newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                        }
                        if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                        }
                        var entry_ = newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age];
                        entry_.biomass += concreteCohort.Data.Biomass - totalBiomassAccountedFor;
                        entry_.additionalParameters = remainingAdditionalParameters;
                        newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = entry_;
                    }
                }

                //rewrite SiteCohorts() regardless of changes
                //TODO: Create a clone of SiteCohorts minus the cohortData
                //seemingly not necessary though
                var newSiteCohorts = new SiteCohorts();
                foreach (var species in newSiteCohortsDictionary) {
                    foreach (var cohort in species.Value) {
                        if (cohort.Value.biomass > 0) {
                            ExpandoObject additionalParameters = new ExpandoObject();
                            IDictionary<string, object> additionalParametersDictionary = (IDictionary<string, object>)additionalParameters;
                            //Console.WriteLine($"F biomass: {cohort.Value.biomass}, age: {cohort.Key}, species: {species.Key.Name}");
                            foreach (var parameter in cohort.Value.additionalParameters) {
                                //Console.WriteLine($"F Parameter: {parameter.Key}, Value: {parameter.Value}");
                                additionalParametersDictionary[parameter.Key] = parameter.Value;
                            }
                            newSiteCohorts.AddNewCohort(species.Key, cohort.Key, cohort.Value.biomass, additionalParameters);
                        }
                    }
                }
                foreach (ISpeciesCohorts speciesCohorts in newSiteCohorts) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    concreteSpeciesCohorts.UpdateMaturePresent();
                }
                Cohorts[site] = newSiteCohorts;
                foreach (var data in newSiteCohortsDictionary) {
                    data.Value.Clear();
                }
            }
            
            int[] mortalityTrackerInt = new int[landscapeSize];
            for (int i = 0; i < landscapeSize; i++) {
                mortalityTrackerInt[i] = (int)Math.Floor(mortalityTracker[i]);
            }
            string outputPath = $"./data/mortality/{PlugIn.ModelCore.CurrentTime}.bin";
            SerializeAsBincode(outputPath, PlugIn.ModelCore.CurrentTime, mortalityTrackerInt);
        }
    }
}
