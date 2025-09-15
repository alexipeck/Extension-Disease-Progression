using System;
using System.Collections.Generic;
using System.IO;
using Landis.Core;
using Landis.Utilities;
using Tomlyn;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public static class TomlInputLoader
    {
        public static IInputParameters Load(string tomlPath, ICore modelCore)
        {
            var text = File.ReadAllText(tomlPath);
            var model = Toml.ToModel(text);

            var parameters = new InputParameters();

            parameters.Timestep = Convert.ToInt32(GetValue(model, "Timestep", required: true));

            var kernelString = Convert.ToString(GetNestedValue(model, new[] { "dispersal", "kernel" }, required: true));
            parameters.DistanceDispersalDecayKernel = ParseKernel(kernelString);

            var maxDistance = GetNestedValue(model, new[] { "dispersal", "max_distance" }, required: true);
            parameters.DispersalMaxDistance = Convert.ToInt32(maxDistance);

            var dispersalTable = GetTable(model, "dispersal", required: true) as IDictionary<string, object>;
            if (dispersalTable == null) throw new InputValueException("dispersal", "Missing required [dispersal] section.");
            if (!dispersalTable.TryGetValue(kernelString, out var kernelObj))
                throw new InputValueException($"dispersal.{kernelString}", $"Missing required [dispersal.{kernelString}] section.");
            var kernelParams = kernelObj as IDictionary<string, object>;
            if (kernelParams == null)
                throw new InputValueException($"dispersal.{kernelString}", $"[dispersal.{kernelString}] must be a table of parameters.");

            switch (parameters.DistanceDispersalDecayKernel)
            {
                case DistanceDispersalDecayKernel.NegativeExponent:
                    if (!kernelParams.TryGetValue("alpha_coefficient", out var a1) || a1 == null)
                        throw new InputValueException("alpha_coefficient", "alpha_coefficient is required for NegativeExponent.");
                    parameters.SetDistanceDispersalDecayKernelFunction(new NegativeExponentKernel(Convert.ToDouble(a1)));
                    break;
                case DistanceDispersalDecayKernel.PowerLaw:
                    if (!kernelParams.TryGetValue("alpha_coefficient", out var a2) || a2 == null)
                        throw new InputValueException("alpha_coefficient", "alpha_coefficient is required for PowerLaw.");
                    parameters.SetDistanceDispersalDecayKernelFunction(new PowerLawKernel(Convert.ToDouble(a2)));
                    break;
                case DistanceDispersalDecayKernel.SingleAnchoredPowerLaw:
                    if (!kernelParams.TryGetValue("min_distance", out var md) || md == null)
                        throw new InputValueException("min_distance", "min_distance is required for AnchoredPowerLaw.");
                    if (!kernelParams.TryGetValue("alpha_coefficient", out var ac) || ac == null)
                        throw new InputValueException("alpha_coefficient", "alpha_coefficient is required for AnchoredPowerLaw.");
                    parameters.SetDistanceDispersalDecayKernelFunction(new SingleAnchoredPowerLawKernel(Convert.ToDouble(md), Convert.ToDouble(ac)));
                    break;
                case DistanceDispersalDecayKernel.DoubleAnchoredPowerLaw:
                    if (!kernelParams.TryGetValue("p1", out var p1) || p1 == null)
                        throw new InputValueException("p1", "p1 is required for DoubleAnchoredPowerLaw.");
                    if (!kernelParams.TryGetValue("p2", out var p2) || p2 == null)
                        throw new InputValueException("p2", "p2 is required for DoubleAnchoredPowerLaw.");
                    if (!kernelParams.TryGetValue("d1", out var d1) || d1 == null)
                        throw new InputValueException("d1", "d1 is required for DoubleAnchoredPowerLaw.");
                    if (!kernelParams.TryGetValue("d2", out var d2) || d2 == null)
                        throw new InputValueException("d2", "d2 is required for DoubleAnchoredPowerLaw.");
                    parameters.SetDistanceDispersalDecayKernelFunction(new DoubleAnchoredPowerLawKernel(Convert.ToDouble(p1), Convert.ToDouble(p2), Convert.ToDouble(d1), Convert.ToDouble(d2)));
                    break;
            }

            var speciesHostIndexPath = Convert.ToString(GetValue(model, "SpeciesHostIndex", required: true));
            var speciesMatrixPath = Convert.ToString(GetValue(model, "SpeciesMatrix", required: true));

            var speciesNameToISpecies = new Dictionary<string, ISpecies>(StringComparer.OrdinalIgnoreCase);
            foreach (var species in PlugIn.ModelCore.Species) speciesNameToISpecies[species.Name] = species;
            speciesNameToISpecies["DEAD"] = null;

            var speciesHostIndex = new Dictionary<ISpecies, HostIndex>();
            int lineNum = -1;
            foreach (string line in File.ReadLines(speciesHostIndexPath))
            {
                lineNum++;
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                string[] columns = trimmed.Split(',');
                if (columns.Length != 7) throw new InputValueException(trimmed, "Invalid number of columns in species competency file");
                if (lineNum == 0)
                {
                    if (columns[0].ToLower() != "species" || columns[1].ToLower() != "lowage" || columns[2].ToLower() != "lowscore" || columns[3].ToLower() != "mediumage" || columns[4].ToLower() != "mediumscore" || columns[5].ToLower() != "highage" || columns[6].ToLower() != "highscore") throw new InputValueException(trimmed, "Invalid field");
                    continue;
                }
                if (!speciesNameToISpecies.TryGetValue(columns[0], out ISpecies species)) throw new InputValueException(columns[0], $"Species '{columns[0]}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.1");
                if (!ushort.TryParse(columns[1], out ushort lowAge)) throw new InputValueException(columns[1], $"Invalid low age value '{columns[1]}' on line {lineNum}, column 2.");
                if (!byte.TryParse(columns[2], out byte lowScore)) throw new InputValueException(columns[2], $"Invalid low score value '{columns[2]}' on line {lineNum}, column 3.");
                if (!ushort.TryParse(columns[3], out ushort mediumAge)) throw new InputValueException(columns[3], $"Invalid medium age value '{columns[3]}' on line {lineNum}, column 4.");
                if (!byte.TryParse(columns[4], out byte mediumScore)) throw new InputValueException(columns[4], $"Invalid medium score value '{columns[4]}' on line {lineNum}, column 5.");
                if (!ushort.TryParse(columns[5], out ushort highAge)) throw new InputValueException(columns[5], $"Invalid high age value '{columns[5]}' on line {lineNum}, column 6.");
                if (!byte.TryParse(columns[6], out byte highScore)) throw new InputValueException(columns[6], $"Invalid high score value '{columns[6]}' on line {lineNum}, column 7.");
                if (lowScore > 10) throw new InputValueException(columns[2], $"Low score value '{columns[2]}' on line {lineNum}, column 3 must be 10 or less.");
                if (mediumScore > 10) throw new InputValueException(columns[4], $"Medium score value '{columns[4]}' on line {lineNum}, column 5 must be 10 or less.");
                if (highScore > 10) throw new InputValueException(columns[6], $"High score value '{columns[6]}' on line {lineNum}, column 7 must be 10 or less.");
                if (lowAge >= 0 && mediumAge >= 0 && lowAge >= mediumAge) throw new InputValueException(trimmed, $"Age values on line {lineNum} must be in ascending order: low < medium (when both low and medium are enabled).");
                if (mediumAge >= 0 && highAge >= 0 && mediumAge >= highAge) throw new InputValueException(trimmed, $"Age values on line {lineNum} must be in ascending order: medium < high (when both medium and high are enabled).");
                if (lowAge >= 0 && highAge >= 0 && lowAge >= highAge) throw new InputValueException(trimmed, $"Age values on line {lineNum} must be in ascending order: low < high (when both low and high are enabled).");
                speciesHostIndex[species] = new HostIndex(new HostIndexEntry(lowAge, lowScore), new HostIndexEntry(mediumAge, mediumScore), new HostIndexEntry(highAge, highScore));
            }
            parameters.SpeciesHostIndex = speciesHostIndex;

            var speciesOrderList = new List<ISpecies>();
            var speciesTransitionMatrix = new Dictionary<ISpecies, List<(ISpecies, double)>>();
            lineNum = -1;
            List<string> columnHeaders = null;
            foreach (string line in File.ReadLines(speciesMatrixPath))
            {
                lineNum++;
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                string[] columns = trimmed.Split(',');
                if (lineNum == 0)
                {
                    columnHeaders = new List<string>(columns);
                    if (columnHeaders.Count < 3) throw new InputValueException(speciesMatrixPath, "Species matrix file must have at least 3 columns (source species, target species, and DEAD).");
                    if (columnHeaders[columnHeaders.Count - 1].ToUpper() != "DEAD") throw new InputValueException(speciesMatrixPath, "Last column must be 'DEAD' (case-insensitive).");
                    continue;
                }
                if (!speciesNameToISpecies.TryGetValue(columns[0], out ISpecies sourceSpecies)) throw new InputValueException(columns[0], $"Species '{columns[0]}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.2");
                speciesOrderList.Add(sourceSpecies);
                speciesTransitionMatrix[sourceSpecies] = new List<(ISpecies, double)>();
                for (int i = 1; i < columns.Length; i++)
                {
                    if (!double.TryParse(columns[i], out double proportion)) throw new InputValueException(columns[i], $"Invalid proportion value '{columns[i]}' on line {lineNum}, column {i + 1}.");
                    if (proportion < 0.0) throw new InputValueException(columns[i], $"Proportion value '{columns[i]}' on line {lineNum}, column {i + 1} must not be less than 0.0");
                    if (proportion > 1.0) throw new InputValueException(columns[i], $"Proportion value '{columns[i]}' on line {lineNum}, column {i + 1} must be less than or equal to 1.0");
                    if (columnHeaders[i].ToUpper() == "DEAD") columnHeaders[i] = "DEAD";
                    if (!speciesNameToISpecies.TryGetValue(columnHeaders[i], out ISpecies targetSpecies)) throw new InputValueException(columnHeaders[i], $"Species '{columnHeaders[i]}' on line {lineNum} of SpeciesMatrix file does not exist in scenario species list.3");
                    if (proportion > 0.0) speciesTransitionMatrix[sourceSpecies].Add((targetSpecies, proportion));
                }
            }
            foreach (var species in speciesTransitionMatrix)
            {
                double totalSpecifiedProportion = 0.0;
                foreach ((ISpecies transitionToSpecies, double proportion) in species.Value) totalSpecifiedProportion += proportion;
                if (totalSpecifiedProportion > 1.0) throw new InputValueException(species.Key.Name, $"Proportions for species '{species.Key.Name}' must sum to 1.0 or less (current sum: {totalSpecifiedProportion}).");
                if (totalSpecifiedProportion != 1.0) species.Value.Insert(0, (null, 1.0 - totalSpecifiedProportion));
            }
            parameters.SpeciesTransitionMatrix = speciesTransitionMatrix;
            parameters.DerivedHealthySpecies = speciesOrderList[0];

            if (parameters.DistanceDispersalDecayKernelFunction == null) throw new InputValueException("dispersal.kernel", "Failed to construct kernel.");

            PlugIn.ModelCore.UI.WriteLine("Configuration summary:");
            PlugIn.ModelCore.UI.WriteLine($"  Timestep: {parameters.Timestep}");
            PlugIn.ModelCore.UI.WriteLine($"  SpeciesHostIndex: {speciesHostIndexPath}");
            PlugIn.ModelCore.UI.WriteLine($"  SpeciesMatrix: {speciesMatrixPath}");
            PlugIn.ModelCore.UI.WriteLine($"  Dispersal kernel: {parameters.DistanceDispersalDecayKernel}");
            PlugIn.ModelCore.UI.WriteLine($"  Dispersal maximum distance: {parameters.DispersalMaxDistance}");
            switch (parameters.DistanceDispersalDecayKernel)
            {
                case DistanceDispersalDecayKernel.NegativeExponent:
                case DistanceDispersalDecayKernel.PowerLaw:
                    PlugIn.ModelCore.UI.WriteLine($"  Kernel: {parameters.DistanceDispersalDecayKernel}");
                    break;
                case DistanceDispersalDecayKernel.SingleAnchoredPowerLaw:
                    PlugIn.ModelCore.UI.WriteLine($"  Kernel: {parameters.DistanceDispersalDecayKernel}");
                    break;
                case DistanceDispersalDecayKernel.DoubleAnchoredPowerLaw:
                    PlugIn.ModelCore.UI.WriteLine($"  Kernel: {parameters.DistanceDispersalDecayKernel}");
                    break;
            }

            return parameters;
        }

        // Validation is enforced during kernel construction; no separate validator required.

        private static object GetValue(IDictionary<string, object> model, string key, bool required)
        {
            foreach (var kv in model)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            if (required) throw new InputValueException(key, $"Missing required key '{key}'.");
            return null;
        }

        private static object GetNestedValue(IDictionary<string, object> model, string[] path, bool required)
        {
            object current = model;
            for (int i = 0; i < path.Length; i++)
            {
                var table = current as IDictionary<string, object>;
                if (table == null)
                {
                    if (required) throw new InputValueException(string.Join(".", path), $"Missing required key '{string.Join(".", path)}'.");
                    return null;
                }
                bool found = false;
                foreach (var kv in table)
                {
                    if (string.Equals(kv.Key, path[i], StringComparison.OrdinalIgnoreCase))
                    {
                        current = kv.Value;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    if (required) throw new InputValueException(string.Join(".", path), $"Missing required key '{string.Join(".", path)}'.");
                    return null;
                }
            }
            return current;
        }

        private static object GetTable(IDictionary<string, object> model, string key, bool required)
        {
            var value = GetValue(model, key, required);
            return value as IDictionary<string, object>;
        }

        private static DistanceDispersalDecayKernel ParseKernel(string text)
        {
            if (string.Equals(text, "NegativeExponent", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.NegativeExponent;
            if (string.Equals(text, "PowerLaw", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.PowerLaw;
            if (string.Equals(text, "AnchoredPowerLaw", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.SingleAnchoredPowerLaw;
            if (string.Equals(text, "DoubleAnchoredPowerLaw", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.DoubleAnchoredPowerLaw;
            throw new FormatException("Valid kernels: NegativeExponent, PowerLaw, AnchoredPowerLaw, DoubleAnchoredPowerLaw");
        }


    }
}

