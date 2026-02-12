using System;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public static class MathGuard
    {
        public static double RequireFinite(double value, string context)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                Log.Error(LogType.Math, $"{context}: non-finite value {value}");
                throw new InvalidOperationException($"{context}: non-finite value {value}");
            }
            return value;
        }

        public static double WarnAndReturnZero(string context)
        {
            Log.Warn(LogType.Math, $"{context}: substituted 0.0");
            return 0.0;
        }

        public static double DivideOrZero(double numerator, double denominator, string context)
        {
            if (denominator == 0.0)
            {
                return WarnAndReturnZero($"{context}: denominator is 0");
            }
            double result = numerator / denominator;
            return RequireFinite(result, context);
        }

        public static double DivideOrZero(double numerator, int denominator, string context)
        {
            if (denominator == 0)
            {
                return WarnAndReturnZero($"{context}: denominator is 0");
            }
            double result = numerator / denominator;
            return RequireFinite(result, context);
        }
    }
}
