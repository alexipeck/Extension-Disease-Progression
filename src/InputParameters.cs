using System.Collections.Generic;
using System;
using Landis.Utilities;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public enum DispersalProbabilityAlgorithm { PowerLaw, NegativeExponent };
    public interface IInputParameters
    {
        int Timestep {get;set;}
        Dictionary<string, List<(string, double)>> SpeciesTransitionMatrix { get; set; }
        List<(string, double)> GetTransitionMatrixDistribution(string speciesName);
        bool TransitionMatrixContainsSpecies(string speciesName);
        string DerivedHealthySpecies { get; set; }
        DispersalProbabilityAlgorithm DispersalProbabilityAlgorithm { get; set; }
        int DispersalMaxDistance { get; set; }
        double AlphaCoefficient { get; set; }
    }
    public class InputParameters
        : IInputParameters
    {
        private int timestep;
        private Dictionary<string, List<(string, double)>> speciesTransitionMatrix;
        private string derivedHealthySpecies;
        private DispersalProbabilityAlgorithm dispersalType;
        private int dispersalMaxDistance;
        private double alphaCoefficient;
        public Dictionary<string, List<(string, double)>> SpeciesTransitionMatrix
        {
            get {
                return speciesTransitionMatrix;
            }
            set {
                speciesTransitionMatrix = value;
            }
        }

        public DispersalProbabilityAlgorithm DispersalProbabilityAlgorithm
        {
            get {
                return dispersalType;
            }
            set {
                dispersalType = value;
            }
        }
        public int DispersalMaxDistance
        {
            get {
                return dispersalMaxDistance;
            }
            set {
                dispersalMaxDistance = value;
            }
        }

        public string DerivedHealthySpecies
        {
            get {
                return derivedHealthySpecies;
            }
            set {
                derivedHealthySpecies = value;
            }
        }
        public double AlphaCoefficient
        {
            get {
                return alphaCoefficient;
            }
            set {
                alphaCoefficient = value;
            }
        }

        public InputParameters() {}
        public int Timestep
        {
            get {
                return timestep;
            }
            set {
                if (value < 0)
                        throw new InputValueException(value.ToString(),
                                                      "Value must be = or > 0.");
                timestep = value;
            }
        }

        public bool TransitionMatrixContainsSpecies(string speciesName) {
            return speciesTransitionMatrix.ContainsKey(speciesName);
        }
        public List<(string, double)> GetTransitionMatrixDistribution(string speciesName) {
            if (!speciesTransitionMatrix.TryGetValue(speciesName, out List<(string, double)> speciesTransitions)) {
                return null;
            }
            return speciesTransitions;
        }
        
    }
}
