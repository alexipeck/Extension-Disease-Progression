using Landis.Core;
using Landis.SpatialModeling;
using Landis.Library.UniversalCohorts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

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
            
            Console.WriteLine($"Generated dispersal matrix with {dispersalLookupMatrix.Count} entries");
            if (dispersalLookupMatrix.Count > 0)
            {
                var nonZeroEntries = dispersalLookupMatrix.Where(kvp => kvp.Value > 0.0).Take(5).ToList();
                Console.WriteLine($"Found {nonZeroEntries.Count} non-zero entries");
                foreach (var entry in nonZeroEntries)
                {
                    Console.WriteLine($"Entry at {entry.Key}: {entry.Value:E6}");
                }
            }
            
            GenerateProbabilityMatrixImage(dispersalLookupMatrix, landscapeX, landscapeY);
            
            return dispersalLookupMatrix;
        }

        private static void GenerateProbabilityMatrixImage(Dictionary<(int x, int y), double> dispersalLookupMatrix, int landscapeX, int landscapeY) {
            int cellSize = 120;
            int matrixWidth = landscapeX + 1;
            int matrixHeight = landscapeY + 1;
            int imageWidth = matrixWidth * cellSize;
            int imageHeight = matrixHeight * cellSize;
            
            int centerX = (landscapeX / 2) + 1;
            int centerY = (landscapeY / 2) + 1;
            
            using (Bitmap bitmap = new Bitmap(imageWidth, imageHeight))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                
                using (Font font = new Font("Arial", 12))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    for (int gridY = 0; gridY < matrixHeight; gridY++)
                    {
                        for (int gridX = 0; gridX < matrixWidth; gridX++)
                        {
                            int offsetX = gridX - centerX;
                            int offsetY = gridY - centerY;
                            
                            if (dispersalLookupMatrix.TryGetValue((offsetX, offsetY), out double probability))
                            {
                                int pixelX = gridX * cellSize;
                                int pixelY = gridY * cellSize;
                                
                                string probabilityText;
                                if (probability == 0.0)
                                {
                                    probabilityText = "0";
                                }
                                else if (probability < 0.00000001)
                                {
                                    probabilityText = "~0";
                                }
                                else
                                {
                                    probabilityText = probability.ToString("G8");
                                    if (probabilityText.StartsWith("0."))
                                    {
                                        probabilityText = probabilityText.Substring(1);
                                    }
                                }
                                
                                SizeF textSize = graphics.MeasureString(probabilityText, font);
                                float textX = pixelX + (cellSize - textSize.Width) / 2;
                                float textY = pixelY + (cellSize - textSize.Height) / 2;
                                
                                graphics.DrawString(probabilityText, font, textBrush, textX, textY);
                            }
                        }
                    }
                }
                
                string filename = $"dispersal_probability_matrix_{landscapeX}x{landscapeY}.png";
                bitmap.Save(filename, ImageFormat.Png);
            }
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
