using Landis.Core;
using Landis.SpatialModeling;
using Landis.Library.UniversalCohorts;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Landis.Extension.Disturbance.DiseaseProgression
{    public static class SiteVars
    {
        private static ISiteVar<SiteCohorts> universalCohorts;
        private static Dictionary<(int x, int y), double> indexOffsetDispersalProbabilityDictionary;

        public static void Initialize(ICore modelCore, IInputParameters parameters) {
            universalCohorts = PlugIn.ModelCore.GetSiteVar<SiteCohorts>("Succession.UniversalCohorts");
            var landscapeDimensions = PlugIn.ModelCore.Landscape.Dimensions;
            (int landscapeX, int landscapeY) = (landscapeDimensions.Rows, landscapeDimensions.Columns);
            indexOffsetDispersalProbabilityDictionary = GenerateDispersalLookupMatrix(parameters.DispersalProbabilityAlgorithm, parameters.AlphaCoefficient, PlugIn.ModelCore.CellLength, landscapeX, landscapeY);
        }

        public static (int x, int y) CalculateRelativeGridOffset(int x1, int y1, int x2, int y2) {
            return (x2 - x1, y2 - y1);
        }

        private static double CalculateEuclideanDistance(int x1, int y1, int x2, int y2) {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }

        public static double GetDispersalProbability(int x, int y) {
            // direct indexing without error case should be safe as a probability
            // should have be engenerated for every valid index
            return indexOffsetDispersalProbabilityDictionary[(x, y)];
        }

        private static Dictionary<(int x, int y), double> GenerateDispersalLookupMatrix(DispersalProbabilityAlgorithm dispersalType, double alphaCoefficient, float cellLength, int landscapeX, int landscapeY) {
            Debug.Assert(cellLength > 0);
            float cellArea = cellLength * cellLength;
            double pythagorasConstant = Math.Sqrt(2);
            //TODO: Check if the alphaCoefficient is supposed to be normalized to 0.0 and 1.0
            int maxRadius = (int)Math.Ceiling(Math.Max(landscapeX, landscapeY) * pythagorasConstant);
            Dictionary<(int x, int y), double> dispersalLookupMatrix = new Dictionary<(int x, int y), double>();
            for (int i = -maxRadius; i <= maxRadius; i++)
            {
                for (int j = -maxRadius; j <= maxRadius; j++)
                {
                    double distance = CalculateEuclideanDistance(i, j, 0, 0) * cellLength;
                    if (distance == 0.0) {
                        dispersalLookupMatrix[(i, j)] = 0.0;
                        continue;
                    }
                    double probability = CalculateDispersalProbability(dispersalType, distance, alphaCoefficient, cellLength, cellArea);
                    dispersalLookupMatrix[(i, j)] = probability;
                }
            }
            return dispersalLookupMatrix;
        }

        private static double CalculateDispersalProbability(DispersalProbabilityAlgorithm dispersalType, double distance, double alphaCoefficient, float cellLength, float cellArea) {
            if (distance == 0.0) {
                return 0.0;
            }
            double density;
            switch(dispersalType) 
            {
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
        public static Dictionary<(int x, int y), double> IndexOffsetDispersalProbabilityDictionary
        {
            get
            {
                return indexOffsetDispersalProbabilityDictionary;
            }
        }
    }
}
