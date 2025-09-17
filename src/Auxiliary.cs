

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Landis.Extension.Disturbance.DiseaseProgression {
    public static class Auxiliary {
        public static (int x, int y) CalculateRelativeGridOffset(int x1, int y1, int x2, int y2) {
            return (x2 - x1, y2 - y1);
        }

        public static (int x, int y) CalculateIndexToCoordinates(int index, int width) {
            return (index % width, index / width);
        }

        public static int CalculateCoordinatesToIndex(int x, int y, int width) {
            return y * width + x;
        }

        public static double CalculateEuclideanDistance(int x1, int y1, int x2, int y2) {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }
        public static double CalculatedEuclideanDistanceUsingGridOffset(int x, int y) {
            return Math.Sqrt(x * x + y * y);
        }
        public static int CalculateHostIndex(HostIndex hostIndex, ushort age) {
            if (age >= hostIndex.High.Age)
                return hostIndex.High.Score;
            else if (age >= hostIndex.Medium.Age)
                return hostIndex.Medium.Score;
            else if (age >= hostIndex.Low.Age)
                return hostIndex.Low.Score;
            return 0;
        }
        public static double CalculateSiteHostIndexModified(double siteHostIndex, double landTypeModifier, double disturbanceModifiersSum) {
            return siteHostIndex + landTypeModifier + disturbanceModifiersSum;
        }
        /* public static (int x, int y) CanonicalizeToHalfQuadrant(int x, int y) {
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
        } */
        public static (int x, int y) CanonicalizeToHalfQuadrant(int x, int y) {
            int ax = x < 0 ? -x : x;
            int ay = y < 0 ? -y : y;
            return ax >= ay ? (ax, ay) : (ay, ax);
        }
        public static void ExportBitmap(double[] data, string filePathPrefix, string label) {
            double[] dataCopy = new double[data.Length];
            Array.Copy(data, dataCopy, data.Length);
            Task.Run(() => {
                Stopwatch outputStopwatch = new Stopwatch();
                outputStopwatch.Start();
                try {
                    string outputPath = $"{filePathPrefix}_{PlugIn.ModelCore.CurrentTime}.png";
                    SiteVars.GenerateStateBitmap(outputPath, dataCopy);
                }
                catch (Exception ex) {
                    PlugIn.ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                    throw;
                }
                outputStopwatch.Stop();
                PlugIn.ModelCore.UI.WriteLine($"      Finished outputting {label} state: {outputStopwatch.ElapsedMilliseconds} ms");
            });
        }
    }
}