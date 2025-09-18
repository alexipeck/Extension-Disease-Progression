

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
        public static string DoubleFormatter(double value) {
            if (value == 1) return "1";
            if (value == 0) return "0";
			int zeros = 0;
			int guard = 0;
			while (value > 0.0 && value < 0.1 && guard < 400) {
				value *= 10.0;
				zeros++;
				guard++;
			}
			double s = value * 10.0;
			int d1 = (int)s; s = (s - d1) * 10.0;
			int d2 = (int)s; s = (s - d2) * 10.0;
			int d3 = (int)s;
			if (d1 < 0) d1 = 0; if (d1 > 9) d1 = 9;
			if (d2 < 0) d2 = 0; if (d2 > 9) d2 = 9;
			if (d3 < 0) d3 = 0; if (d3 > 9) d3 = 9;
			int eTmp = zeros;
			int eLen = 0;
			do { eLen++; eTmp /= 10; } while (eTmp > 0);
			char[] buf = new char[4 + eLen];
			buf[0] = (char)('0' + d1);
			buf[1] = (char)('0' + d2);
			buf[2] = (char)('0' + d3);
			buf[3] = '-';
			int j = 3 + eLen;
			int e = zeros;
			while (j > 3) {
				int digit = e % 10;
				buf[j] = (char)('0' + digit);
				e /= 10;
				j--;
			}
			return new string(buf);
        }
    }
}