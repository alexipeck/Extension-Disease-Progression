using System.Collections.Generic;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public interface IInputParameters
    {
        int Timestep {get;set;}
        Dictionary<string, Dictionary<string, double>> SpeciesTransitionMatrix { get; set; }
        string GetTransitionMatrixOutcome(string speciesName, bool outputProbability);
    }
}
