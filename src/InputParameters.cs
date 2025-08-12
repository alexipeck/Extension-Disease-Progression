using System.Collections.Generic;
using System;
using Landis.Utilities;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public class InputParameters
        : IInputParameters
    {
        private int timestep;
        private Dictionary<string, Dictionary<string, double>> speciesTransitionMatrix;
        public Dictionary<string, Dictionary<string, double>> SpeciesTransitionMatrix
        {
            get {
                return speciesTransitionMatrix;
            }
            set {
                speciesTransitionMatrix = value;
            }
        }

        public InputParameters()
        {}
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
