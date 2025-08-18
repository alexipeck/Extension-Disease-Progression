
using Landis.Core;
using System.Collections.Generic;
using Landis.Utilities;
using System.Diagnostics;

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
            Type.SetDescription<DispersalProbabilityAlgorithm>("Dispersal Probability Algorithm");
            InputValues.Register<DispersalProbabilityAlgorithm>(DispersalProbabilityAlgorithmParser);
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
            var speciesTransitionMatrix = new Dictionary<string, List<(string, double)>>();
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
                speciesTransitionMatrix[sourceSpecies] = new List<(string, double)>();
                
                for (int i = 1; i < columns.Length; i++) {
                    if (!double.TryParse(columns[i], out double proportion)) {
                        throw new InputValueException(columns[i], $"Invalid proportion value '{columns[i]}' on line {lineNum}, column {i + 1}.");
                    }
                    if (proportion < 0.0) {
                        throw new InputValueException(columns[i], $"Proportion value '{columns[i]}' on line {lineNum}, column {i + 1} must not be less than 0.0");
                    }
                    if (proportion > 1.0) {
                        throw new InputValueException(columns[i], $"Proportion value '{columns[i]}' on line {lineNum}, column {i + 1} must be less than or equal to 1.0");
                    }
                    
                    var targetSpecies = columnHeaders[i];
                    if (proportion > 0.0) {
                        speciesTransitionMatrix[sourceSpecies].Add((targetSpecies, proportion));
                    }
                }
            }
            foreach (var species in speciesTransitionMatrix) {
                double totalSpecifiedProportion = 0.0;
                foreach ((string transitionToSpecies, double proportion) in species.Value) {
                    totalSpecifiedProportion += proportion;
                }
                if (totalSpecifiedProportion > 1.0) {
                    throw new InputValueException(species.Key, $"Proportions for species '{species.Key}' must sum to 1.0 or less (current sum: {totalSpecifiedProportion}).");
                }
                if (totalSpecifiedProportion != 1.0) {
                    species.Value.Insert(0, (null, 1.0 - totalSpecifiedProportion));
                    PlugIn.ModelCore.UI.WriteLine($"Adding remaining proportion for {species.Key} ({1.0 - totalSpecifiedProportion}) to indicate no change.");
                }
            }
            parameters.SpeciesTransitionMatrix = speciesTransitionMatrix;
            Debug.Assert(speciesOrderList.Count >= 2);
            PlugIn.ModelCore.UI.WriteLine($"Derived healthy species: {speciesOrderList[0]}");
            parameters.DerivedHealthySpecies = speciesOrderList[0];
            
            PlugIn.ModelCore.UI.WriteLine("Species Transition Matrix:");
            foreach (var outerEntry in speciesTransitionMatrix)
            {
                PlugIn.ModelCore.UI.WriteLine($"  Source Species: {outerEntry.Key}");
                foreach ((string transitionToSpecies, double proportion) in outerEntry.Value)
                {
                    PlugIn.ModelCore.UI.WriteLine($"    Target: {((transitionToSpecies == null) ? outerEntry.Key : transitionToSpecies)}, Proportion: {proportion * 100}%");
                }
            }            
            PlugIn.ModelCore.UI.WriteLine("Finished reading species matrix file");

            InputVar<DispersalProbabilityAlgorithm> dispersalType = new InputVar<DispersalProbabilityAlgorithm>("DispersalProbabilityAlgorithm");
            ReadVar(dispersalType);
            PlugIn.ModelCore.UI.WriteLine($"Dispersal type: {dispersalType.Value}");
            parameters.DispersalProbabilityAlgorithm = dispersalType.Value;

            InputVar<int> dispersalMaximumDistance = new InputVar<int>("DispersalMaximumDistance");
            ReadVar(dispersalMaximumDistance);
            PlugIn.ModelCore.UI.WriteLine($"Dispersal maximum distance: {dispersalMaximumDistance.Value}");
            parameters.DispersalMaxDistance = dispersalMaximumDistance.Value;

            InputVar<double> alpha_coefficient = new InputVar<double>("AlphaCoefficient");
            ReadVar(alpha_coefficient); 
            PlugIn.ModelCore.UI.WriteLine($"Alpha coefficient: {alpha_coefficient.Value}");
            parameters.AlphaCoefficient = alpha_coefficient.Value;
            
            return parameters;
        }

        public static DispersalProbabilityAlgorithm DispersalProbabilityAlgorithmParser(string text)
        {
            if (text == "NegativeExponent")
                return DispersalProbabilityAlgorithm.NegativeExponent;
            else if (text == "PowerLaw")
                return DispersalProbabilityAlgorithm.PowerLaw;
            throw new System.FormatException("Valid algorithms: NegativeExponent, PowerLaw");
        }
    }
}
