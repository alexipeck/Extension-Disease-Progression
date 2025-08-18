using System.Collections.Generic;
using System;
using Landis.Utilities;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public enum DispersalProbabilityAlgorithm { PowerLaw, NegativeExponent };
    public interface IInputParameters
    {
        int Timestep {get;set;}
        Dictionary<string, Dictionary<string, double>> SpeciesTransitionMatrix { get; set; }
        string GetTransitionMatrixOutcome(string speciesName, bool outputProbability);
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
        private Dictionary<string, Dictionary<string, double>> speciesTransitionMatrix;
        private string derivedHealthySpecies;
        private DispersalProbabilityAlgorithm dispersalType;
        private int dispersalMaxDistance;
        private double alphaCoefficient;
        public Dictionary<string, Dictionary<string, double>> SpeciesTransitionMatrix
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
        public string GetTransitionMatrixOutcome(string speciesName, bool outputProbability) {
            if (!speciesTransitionMatrix.TryGetValue(speciesName, out Dictionary<string, double> species_transitions)) {
                return null;
            }
            Random rand = new Random();
            double random = rand.NextDouble();
            double cumulativeCheck = 0.0;
            foreach (var transition in species_transitions) {
                cumulativeCheck += transition.Value;
                if (random <= cumulativeCheck) {
                    if (transition.Key == speciesName) {
                        return null;
                    }
                    if (outputProbability) {
                        PlugIn.ModelCore.UI.WriteLine($"Transitioning {speciesName} to {transition.Key} based on a {transition.Value * 100}% probability");
                    }
                    return transition.Key;
                }
            }
            return null;
        }
        
    }
}
