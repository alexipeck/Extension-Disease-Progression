
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
        : Utilities.TextParser<IInputParameters>
    {
        public static class Names
        {
            public const string SpeciesHostIndex = "SpeciesHostIndex";
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
            Type.SetDescription<DispersalProbabilityKernel>("Dispersal Probability Algorithm");
            InputValues.Register<DispersalProbabilityKernel>(DispersalProbabilityKernelParser);
            InputParameters parameters = new InputParameters();

            InputVar<int> timestep = new InputVar<int>("Timestep");
            ReadVar(timestep);
            parameters.Timestep = timestep.Value;

            Dictionary<string, ISpecies> speciesNameToISpecies = new Dictionary<string, ISpecies>();
            foreach (var species in PlugIn.ModelCore.Species) {
                speciesNameToISpecies[species.Name] = species;
            }
            speciesNameToISpecies["DEAD"] = null;

            ////////////////////
            // species matrix

            PlugIn.ModelCore.UI.WriteLine("Started reading species competency file");
            InputVar<string> speciesHostIndexFile = new InputVar<string>(Names.SpeciesHostIndex);
            ReadVar(speciesHostIndexFile);
            //Import CSV data here
            int lineNum = -1;
            var speciesHostIndex = new Dictionary<ISpecies, HostIndex>();
            foreach (string line in System.IO.File.ReadLines(speciesHostIndexFile.Value)) {
                lineNum++;
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                PlugIn.ModelCore.UI.WriteLine($"Processing line {lineNum}: '{trimmed}'");
                string[] columns = trimmed.Split(',');
                if (columns.Length != 7) throw new InputValueException(trimmed, "Invalid number of columns in species competency file");
                if (lineNum == 0) {
                    PlugIn.ModelCore.UI.WriteLine($"Columns: {string.Join(", ", columns)}");
                    if (
                        columns[0].ToLower() != "species" ||
                        columns[1].ToLower() != "lowage" ||
                        columns[2].ToLower() != "lowscore" ||
                        columns[3].ToLower() != "mediumage" ||
                        columns[4].ToLower() != "mediumscore" ||
                        columns[5].ToLower() != "highage" ||
                        columns[6].ToLower() != "highscore"
                    ) throw new InputValueException(trimmed, "Invalid field");
                    continue;
                }

                //parse species
                if (!speciesNameToISpecies.TryGetValue(columns[0], out ISpecies species)) {
                    throw new InputValueException(columns[0], $"Species '{columns[0]}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.1");
                }

                //parse numbers
                if (!ushort.TryParse(columns[1], out ushort lowAge)) {
                    throw new InputValueException(columns[1], $"Invalid low age value '{columns[1]}' on line {lineNum}, column 2.");
                }
                if (!byte.TryParse(columns[2], out byte lowScore)) {
                    throw new InputValueException(columns[2], $"Invalid low score value '{columns[2]}' on line {lineNum}, column 3.");
                }
                if (!ushort.TryParse(columns[3], out ushort mediumAge)) {
                    throw new InputValueException(columns[3], $"Invalid medium age value '{columns[3]}' on line {lineNum}, column 4.");
                }
                if (!byte.TryParse(columns[4], out byte mediumScore)) {
                    throw new InputValueException(columns[4], $"Invalid medium score value '{columns[4]}' on line {lineNum}, column 5.");
                }
                if (!ushort.TryParse(columns[5], out ushort highAge)) {
                    throw new InputValueException(columns[5], $"Invalid high age value '{columns[5]}' on line {lineNum}, column 6.");
                }
                if (!byte.TryParse(columns[6], out byte highScore)) {
                    throw new InputValueException(columns[6], $"Invalid high score value '{columns[6]}' on line {lineNum}, column 7.");
                }
                if (lowScore > 10) {
                    throw new InputValueException(columns[2], $"Low score value '{columns[2]}' on line {lineNum}, column 3 must be 10 or less.");
                }
                if (mediumScore > 10) {
                    throw new InputValueException(columns[4], $"Medium score value '{columns[4]}' on line {lineNum}, column 5 must be 10 or less.");
                }
                if (highScore > 10) {
                    throw new InputValueException(columns[6], $"High score value '{columns[6]}' on line {lineNum}, column 7 must be 10 or less.");
                }
                if (lowAge >= 0 && mediumAge >= 0 && lowAge >= mediumAge) {
                    throw new InputValueException(trimmed, $"Age values on line {lineNum} must be in ascending order: low < medium (when both low and medium are enabled).");
                }
                if (mediumAge >= 0 && highAge >= 0 && mediumAge >= highAge) {
                    throw new InputValueException(trimmed, $"Age values on line {lineNum} must be in ascending order: medium < high (when both medium and high are enabled).");
                }
                if (lowAge >= 0 && highAge >= 0 && lowAge >= highAge) {
                    throw new InputValueException(trimmed, $"Age values on line {lineNum} must be in ascending order: low < high (when both low and high are enabled).");
                }
                PlugIn.ModelCore.UI.WriteLine($"Adding species {species.Name} to species competency with low age {lowAge}, low score {lowScore}, medium age {mediumAge}, medium score {mediumScore}, high age {highAge}, high score {highScore}");
                speciesHostIndex[species] = new HostIndex(
                    new HostIndexEntry(lowAge, lowScore),
                    new HostIndexEntry(mediumAge, mediumScore),
                    new HostIndexEntry(highAge, highScore)
                );
            }
            parameters.SpeciesHostIndex = speciesHostIndex;
            PlugIn.ModelCore.UI.WriteLine("Finished reading species competency file");

            // read file
            PlugIn.ModelCore.UI.WriteLine("Started reading species matrix file");
            InputVar<string> speciesMatrixFile = new InputVar<string>(Names.SpeciesMatrix);
            ReadVar(speciesMatrixFile);
            
            // dynamically sized matrix ingestion
            List<ISpecies> speciesOrderList = new List<ISpecies>();
            Dictionary<ISpecies, List<(ISpecies, double)>> speciesTransitionMatrix = new Dictionary<ISpecies, List<(ISpecies, double)>>();
            lineNum = -1;
            List<string> columnHeaders = null;
            
            foreach (string line in System.IO.File.ReadLines(speciesMatrixFile.Value)) {
                lineNum++;
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                PlugIn.ModelCore.UI.WriteLine($"Processing line {lineNum}: {trimmed}");
                
                string[] columns = trimmed.Split(',');
                PlugIn.ModelCore.UI.WriteLine($"Columns: {string.Join(", ", columns)}");
                
                if (lineNum == 0) {
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

                if (!speciesNameToISpecies.TryGetValue(columns[0], out ISpecies sourceSpecies)) {
                    throw new InputValueException(columns[0], $"Species '{columns[0]}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.2");
                }
                
                speciesOrderList.Add(sourceSpecies);
                speciesTransitionMatrix[sourceSpecies] = new List<(ISpecies, double)>();
                
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
                    if (columnHeaders[i].ToUpper() == "DEAD") {
                        columnHeaders[i] = "DEAD";
                    }
                    if (!speciesNameToISpecies.TryGetValue(columnHeaders[i], out ISpecies targetSpecies)) {
                        throw new InputValueException(columnHeaders[i], $"Species '{columnHeaders[i]}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.3");
                    }
                    if (proportion > 0.0) {
                        speciesTransitionMatrix[sourceSpecies].Add((targetSpecies, proportion));
                    }
                }
            }
            foreach (var species in speciesTransitionMatrix) {
                double totalSpecifiedProportion = 0.0;
                foreach ((ISpecies transitionToSpecies, double proportion) in species.Value) {
                    totalSpecifiedProportion += proportion;
                }
                if (totalSpecifiedProportion > 1.0) {
                    throw new InputValueException(species.Key.Name, $"Proportions for species '{species.Key.Name}' must sum to 1.0 or less (current sum: {totalSpecifiedProportion}).");
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
                foreach ((ISpecies transitionToSpecies, double proportion) in outerEntry.Value)
                {
                    PlugIn.ModelCore.UI.WriteLine($"    Target: {((transitionToSpecies == null) ? outerEntry.Key : transitionToSpecies)}, Proportion: {proportion * 100}%");
                }
            }            
            PlugIn.ModelCore.UI.WriteLine("Finished reading species matrix file");

            InputVar<DispersalProbabilityKernel> dispersalKernel = new InputVar<DispersalProbabilityKernel>("DispersalProbabilityKernel");
            ReadVar(dispersalKernel);
            PlugIn.ModelCore.UI.WriteLine($"Dispersal type: {dispersalKernel.Value}");
            parameters.DispersalProbabilityKernel = dispersalKernel.Value;

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

        public static DispersalProbabilityKernel DispersalProbabilityKernelParser(string text)
        {
            if (text == "NegativeExponent")
                return DispersalProbabilityKernel.NegativeExponent;
            else if (text == "PowerLaw")
                return DispersalProbabilityKernel.PowerLaw;
            else if (text == "SingleAnchoredPowerLaw")
                return DispersalProbabilityKernel.SingleAnchoredPowerLaw;
            else if (text == "DoubleAnchoredPowerLaw")
                return DispersalProbabilityKernel.DoubleAnchoredPowerLaw;
            throw new System.FormatException("Valid kernels: NegativeExponent, PowerLaw, SingleAnchoredPowerLaw, DoubleAnchoredPowerLaw");
        }
    }
}
