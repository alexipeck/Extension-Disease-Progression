using System;
using Landis.Utilities;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public interface IDistanceDispersalDecayKernel
    {
        double Compute(double distance);
    }

    public sealed class NegativeExponentKernel : IDistanceDispersalDecayKernel
    {
        public double Alpha { get; }
        public NegativeExponentKernel(double alpha)
        {
            if (alpha <= 0) throw new InputValueException("alpha_coefficient", "alpha_coefficient must be > 0.");
            Alpha = alpha;
        }
        public double Compute(double distance) => Math.Exp(-Alpha * distance);
    }

    public sealed class PowerLawKernel : IDistanceDispersalDecayKernel
    {
        public double Alpha { get; }
        public PowerLawKernel(double alpha)
        {
            if (alpha <= 0) throw new InputValueException("alpha_coefficient", "alpha_coefficient must be > 0.");
            Alpha = alpha;
        }
        public double Compute(double distance) => 1.0 / Math.Pow(distance, Alpha);
    }

    public sealed class SingleAnchoredPowerLawKernel : IDistanceDispersalDecayKernel
    {
        public double MinDistance { get; }
        public double Coefficient { get; }
        public SingleAnchoredPowerLawKernel(double minDistance, double coefficient)
        {
            if (minDistance <= 0) throw new InputValueException("min_distance", "min_distance must be > 0.");
            if (coefficient <= 0) throw new InputValueException("alpha_coefficient", "alpha_coefficient must be > 0.");
            MinDistance = minDistance;
            Coefficient = coefficient;
        }
        public double Compute(double distance) => distance <= MinDistance ? 1.0 : Math.Pow(MinDistance / distance, Coefficient);
    }

    public sealed class DoubleAnchoredPowerLawKernel : IDistanceDispersalDecayKernel
    {
        public double K { get; }
        public double A { get; }
        public DoubleAnchoredPowerLawKernel(double p1, double p2, double d1, double d2)
        {
            if (d1 <= 0 || d2 <= 0 || d2 <= d1) throw new InputValueException("d1/d2", "Require d1 > 0, d2 > 0, d2 > d1.");
            if (p1 <= 0 || p2 <= 0) throw new InputValueException("p1/p2", "Require p1 > 0 and p2 > 0.");
            K = Math.Log(p1 / p2) / Math.Log(d2 / d1);
            A = p1 * Math.Pow(d1, K);
        }
        public double Compute(double distance) => A * Math.Pow(distance, -K);
    }
}


