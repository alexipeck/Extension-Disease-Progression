
using Landis.Core;
using System.Collections.Generic;
using Landis.Utilities;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    /// <summary>
    /// A parser that reads biomass succession parameters from text input.
    /// </summary>
    public class InputParametersParser
        : Landis.Utilities.TextParser<IInputParameters>
    {
        public static class Names
        {
            public const string SpeciesMatrix = "SpeciesMatrix";

        }
        private ISpeciesDataset speciesDataset;
        public InputParametersParser()
        {
            this.speciesDataset = PlugIn.ModelCore.Species;
        }

        //---------------------------------------------------------------------

        protected override IInputParameters Parse()
        {
            InputParameters parameters = new InputParameters();

            InputVar<int> timestep = new InputVar<int>("Timestep");
            ReadVar(timestep);
            parameters.Timestep = timestep.Value;
            ////////////////////
            // species matrix

            // read file
            PlugIn.ModelCore.UI.WriteLine("Started reading species matrix file");
            InputVar<string> speciesMatrixFile = new InputVar<string>(Names.SpeciesMatrix);
            ReadVar(speciesMatrixFile);
            
            // dynamically sized matrix ingestion
            var speciesOrderList = new List<string>();
            var speciesTransitionMatrix = new Dictionary<string, Dictionary<string, double>>();
            int lineNum = 0;
            List<string> columnHeaders = null;
            
            foreach (var line in System.IO.File.ReadLines(speciesMatrixFile.Value)) {
                lineNum++;
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                PlugIn.ModelCore.UI.WriteLine($"Processing line {lineNum}: {trimmed}");
                
                var columns = trimmed.Split(',');
                PlugIn.ModelCore.UI.WriteLine($"Columns: {string.Join(", ", columns)}");
                
                if (lineNum == 1) {
                    columnHeaders = new List<string>(columns);
                    if (columnHeaders.Count < 3)
                    {
                        throw new InputValueException(speciesMatrixFile.Value, "Species matrix file must have at least 3 columns (source species, target species, and DEAD).");
                    }
                    if (columnHeaders[columnHeaders.Count - 1].ToUpper() != "DEAD")
                    {
                        throw new InputValueException(speciesMatrixFile.Value, "Last column must be 'DEAD' (case-insensitive).");
                    }
                    continue;
                }
                
                var sourceSpecies = columns[0];
                var found = false;
                foreach (var species in speciesDataset) {
                    if (species.Name == sourceSpecies)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    throw new InputValueException(sourceSpecies, $"Species '{sourceSpecies}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.");
                }
                
                speciesOrderList.Add(sourceSpecies);
                speciesTransitionMatrix[sourceSpecies] = new Dictionary<string, double>();
                
                for (int i = 1; i < columns.Length; i++) {
                    if (!double.TryParse(columns[i], out double probability)) {
                        throw new InputValueException(columns[i], $"Invalid probability value '{columns[i]}' on line {lineNum}, column {i + 1}.");
                    }
                    if (probability < 0.0) {
                        throw new InputValueException(columns[i], $"Probability value '{columns[i]}' on line {lineNum}, column {i + 1} must not be less than 0.0");
                    }
                    if (probability > 1.0) {
                        throw new InputValueException(columns[i], $"Probability value '{columns[i]}' on line {lineNum}, column {i + 1} must be less than or equal to 1.0");
                    }
                    
                    var targetSpecies = columnHeaders[i];
                    if (probability > 0.0) {
                        speciesTransitionMatrix[sourceSpecies][targetSpecies] = probability;
                    }
                }
            }
            foreach (var species in speciesTransitionMatrix) {
                double totalProbability = 0.0;
                foreach (var transition in species.Value) {
                    totalProbability += transition.Value;
                }
                if (totalProbability > 1.0) {
                    throw new InputValueException(species.Key, $"Probabilities for species '{species.Key}' must sum to 1.0 or less (current sum: {totalProbability}).");
                }
            }
            parameters.SpeciesTransitionMatrix = speciesTransitionMatrix;
            
            PlugIn.ModelCore.UI.WriteLine("Species Transition Matrix:");
            foreach (var outerEntry in speciesTransitionMatrix)
            {
                PlugIn.ModelCore.UI.WriteLine($"  Source Species: {outerEntry.Key}");
                foreach (var innerEntry in outerEntry.Value)
                {
                    if (outerEntry.Key != innerEntry.Key) {
                        PlugIn.ModelCore.UI.WriteLine($"    Target: {innerEntry.Key}, Probability: {innerEntry.Value * 100}%");
                    }
                }
            }
            
            PlugIn.ModelCore.UI.WriteLine("Finished reading species matrix file");
            
            return parameters;
        }
    }
}
