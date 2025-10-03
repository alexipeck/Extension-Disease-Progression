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

            parameters.Timestep = Convert.ToInt32(GetValue(model, "timestep", required: true));
            var transmissionRate = GetValue(model, "transmission_rate", required: true);
            if (transmissionRate is double dtr) parameters.TransmissionRate = dtr;
            else throw new InputValueException("transmission_rate", "Value must be a double (unquoted).");
            var shiModeStr = Convert.ToString(GetValue(model, "shi_mode", required: true));
            if (string.IsNullOrWhiteSpace(shiModeStr)) throw new InputValueException("shi_mode", "Missing required key 'shi_mode'.");
            shiModeStr = shiModeStr.ToLowerInvariant();
            if (shiModeStr == "mean") parameters.SHIMode = SHIMode.Mean; else if (shiModeStr == "max") parameters.SHIMode = SHIMode.Max; else throw new InputValueException("shi_mode", "Valid values for 'shi_mode' are 'mean' or 'max'.");

            var kernelString = Convert.ToString(GetNestedValue(model, new[] { "dispersal", "kernel" }, required: true));
            parameters.DistanceDispersalDecayKernel = ParseKernel(kernelString);

            var maxDistance = GetNestedValue(model, new[] { "dispersal", "max_distance" }, required: true);
            parameters.DispersalMaxDistance = Convert.ToInt32(maxDistance);

            var dispersalTable = GetTable(model, "dispersal", required: true) as IDictionary<string, object>;
            if (dispersalTable == null) throw new InputValueException("dispersal", "Missing required [dispersal] section.");
            string kernelKey = (kernelString ?? string.Empty).Trim().ToLowerInvariant();
            if (!TryGetCaseInsensitive(dispersalTable, kernelKey, out var kernelObj))
                throw new InputValueException($"dispersal.{kernelKey}", $"Missing required [dispersal.{kernelKey}] section.");
            var kernelParams = kernelObj as IDictionary<string, object>;
            if (kernelParams == null)
                throw new InputValueException($"dispersal.{kernelKey}", $"[dispersal.{kernelKey}] must be a table of parameters.");

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

            var speciesHostIndexPath = Convert.ToString(GetValue(model, "species_host_index", required: true));

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
                    string[] expected = new[] { "species", "lowage", "lowscore", "mediumage", "mediumscore", "highage", "highscore" };
                    if (columns.Length != expected.Length) throw new InputValueException(trimmed, $"Invalid header: expected {expected.Length} columns [{string.Join(", ", expected)}], found {columns.Length}.");
                    for (int c = 0; c < expected.Length; c++) {
                        if (!columns[c].Equals(expected[c], StringComparison.OrdinalIgnoreCase)) {
                            throw new InputValueException(columns[c], $"Invalid header column {c + 1}: expected '{expected[c]}', found '{columns[c]}'.");
                        }
                    }
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

            var transitionTable = GetTable(model, "transition", required: true) as IDictionary<string, object>;
            if (transitionTable == null) throw new InputValueException("transition", "Missing required [transition] section.");
            var validationDefaultsTable = GetNestedValue(transitionTable, new[] { "validation", "default" }, required: true) as IDictionary<string, object>;
            if (validationDefaultsTable == null) throw new InputValueException("transition.validation.default", "Missing required [transition.validation.default] section.");

            bool exhaustiveProbability = false;
            if (!validationDefaultsTable.TryGetValue("exhaustive_probability", out var exhaustiveObj) || exhaustiveObj == null) throw new InputValueException("transition.validation.default.exhaustive_probability", "Missing required key 'exhaustive_probability'.");
            exhaustiveProbability = Convert.ToBoolean(exhaustiveObj);

            string defaultBelow = Convert.ToString(validationDefaultsTable.ContainsKey("missing_below_range_method") ? validationDefaultsTable["missing_below_range_method"] : "error");
            string defaultInRange = Convert.ToString(validationDefaultsTable.ContainsKey("missing_in_range_method") ? validationDefaultsTable["missing_in_range_method"] : "error");
            string defaultAbove = Convert.ToString(validationDefaultsTable.ContainsKey("missing_above_range_method") ? validationDefaultsTable["missing_above_range_method"] : "error");

            MissingBelowRangeMethod ParseBelow(string s)
            {
                if (string.Equals(s, "ignore", StringComparison.OrdinalIgnoreCase)) return MissingBelowRangeMethod.Ignore;
                if (string.Equals(s, "error", StringComparison.OrdinalIgnoreCase)) return MissingBelowRangeMethod.Error;
                throw new InputValueException("transition.validation.default.missing_below_range_method", "Valid values: ignore, error");
            }
            MissingInRangeMethod ParseInRange(string s)
            {
                if (string.Equals(s, "error", StringComparison.OrdinalIgnoreCase)) return MissingInRangeMethod.Error;
                if (string.Equals(s, "age_threshold", StringComparison.OrdinalIgnoreCase)) return MissingInRangeMethod.AgeThreshold;
                if (string.Equals(s, "linear_interpolation", StringComparison.OrdinalIgnoreCase)) return MissingInRangeMethod.LinearInterpolation;
                throw new InputValueException("transition.validation.default.missing_in_range_method", "Valid values: error, age_threshold, linear_interpolation");
            }
            MissingAboveRangeMethod ParseAbove(string s)
            {
                if (string.Equals(s, "error", StringComparison.OrdinalIgnoreCase)) return MissingAboveRangeMethod.Error;
                if (string.Equals(s, "use_oldest", StringComparison.OrdinalIgnoreCase)) return MissingAboveRangeMethod.UseOldest;
                if (string.Equals(s, "kill_all", StringComparison.OrdinalIgnoreCase)) return MissingAboveRangeMethod.KillAll;
                if (string.Equals(s, "ignore", StringComparison.OrdinalIgnoreCase)) return MissingAboveRangeMethod.Ignore;
                throw new InputValueException("transition.validation.default.missing_above_range_method", "Valid values: error, use_oldest, kill_all, ignore");
            }

            var groupTable = GetNestedValue(transitionTable, new[] { "group" }, required: true) as IDictionary<string, object>;
            if (groupTable == null) throw new InputValueException("transition.group", "Missing required [transition.group] section.");

            var speciesToGroup = new Dictionary<ISpecies, string>();
            var groupHealthy = new Dictionary<string, ISpecies>(StringComparer.OrdinalIgnoreCase);
            var groupInfected = new Dictionary<string, HashSet<ISpecies>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groupTable)
            {
                var groupName = kv.Key;
                var groupConfig = kv.Value as IDictionary<string, object>;
                if (groupConfig == null) throw new InputValueException($"transition.group.{groupName}", $"[transition.group.{groupName}] must be a table.");
                if (!groupConfig.TryGetValue("healthy_species", out var healthyObj) || healthyObj == null) throw new InputValueException($"transition.group.{groupName}.healthy_species", "Missing required key 'healthy_species'.");
                string healthyName = Convert.ToString(healthyObj);
                if (!speciesNameToISpecies.TryGetValue(healthyName, out ISpecies healthySpecies)) throw new InputValueException(healthyName, $"Species '{healthyName}' in transition.group.{groupName}.healthy_species does not exist.");
                groupHealthy[groupName] = healthySpecies;
                if (!groupInfected.ContainsKey(groupName)) groupInfected[groupName] = new HashSet<ISpecies>();
                if (!groupConfig.TryGetValue("infected_pseudo_species", out var infectedObj) || infectedObj == null) throw new InputValueException($"transition.group.{groupName}.infected_pseudo_species", "Missing required key 'infected_pseudo_species'.");
                if (infectedObj is IEnumerable<object> arr)
                {
                    foreach (var sp in arr)
                    {
                        string n = Convert.ToString(sp);
                        if (!speciesNameToISpecies.TryGetValue(n, out ISpecies sps)) throw new InputValueException(n, $"Species '{n}' in transition.group.{groupName}.infected_pseudo_species does not exist.");
                        if (speciesToGroup.ContainsKey(sps)) throw new InputValueException(n, $"Species '{n}' is assigned to multiple groups.");
                        speciesToGroup[sps] = groupName;
                        groupInfected[groupName].Add(sps);
                    }
                }
                else throw new InputValueException($"transition.group.{groupName}.infected_pseudo_species", "infected_pseudo_species must be an array.");
                if (speciesToGroup.ContainsKey(healthySpecies)) throw new InputValueException(healthyName, $"Species '{healthyName}' is assigned to multiple groups.");
                speciesToGroup[healthySpecies] = groupName;
            }

            var dataTable = GetNestedValue(transitionTable, new[] { "data" }, required: true) as IDictionary<string, object>;
            if (dataTable == null) throw new InputValueException("transition.data", "Missing required [transition.data] section.");

            var infectedSpeciesLookup = new HashSet<ISpecies>();
            foreach (var g in groupInfected) foreach (var s in g.Value) infectedSpeciesLookup.Add(s);
            parameters.InfectedSpeciesLookup = infectedSpeciesLookup;
            parameters.DerivedHealthySpecies = new List<ISpecies>(groupHealthy.Values).ToArray();

            var speciesMatrices = new Dictionary<ISpecies, SpeciesAgeMatrix>();

            MissingBelowRangeMethod defaultBelowEnum = ParseBelow(defaultBelow);
            MissingInRangeMethod defaultInRangeEnum = ParseInRange(defaultInRange);
            MissingAboveRangeMethod defaultAboveEnum = ParseAbove(defaultAbove);

            foreach (var kv in dataTable)
            {
                string sourceName = kv.Key;
                if (!speciesNameToISpecies.TryGetValue(sourceName, out ISpecies sourceSpecies)) throw new InputValueException(sourceName, $"Species '{sourceName}' in [transition.data.{sourceName}] does not exist.");
                if (!speciesToGroup.TryGetValue(sourceSpecies, out string groupName)) throw new InputValueException(sourceName, $"Species '{sourceName}' in [transition.data.{sourceName}] is not assigned to any group.");
                var table = kv.Value as IDictionary<string, object>;
                if (table == null) throw new InputValueException($"transition.data.{sourceName}", $"[transition.data.{sourceName}] must be a table.");

                MissingBelowRangeMethod below = defaultBelowEnum;
                MissingInRangeMethod inRange = defaultInRangeEnum;
                MissingAboveRangeMethod above = defaultAboveEnum;
                if (groupTable[groupName] is IDictionary<string, object> groupCfg)
                {
                    if (groupCfg.TryGetValue("missing_below_range_method", out var gb) && gb != null) below = ParseBelow(Convert.ToString(gb));
                    if (groupCfg.TryGetValue("missing_in_range_method", out var gi) && gi != null) inRange = ParseInRange(Convert.ToString(gi));
                    if (groupCfg.TryGetValue("missing_above_range_method", out var ga) && ga != null) above = ParseAbove(Convert.ToString(ga));
                }

                var ageMatrix = new Dictionary<ushort, (ISpecies, double)[]>();
                foreach (var ageKv in table)
                {
                    if (!ushort.TryParse(ageKv.Key.Trim(), out ushort ageKey)) throw new InputValueException(ageKv.Key, $"Invalid age key '{ageKv.Key}' in [transition.data.{sourceName}].");
                    var map = ageKv.Value as IDictionary<string, object>;
                    if (map == null) throw new InputValueException($"transition.data.{sourceName}.{ageKv.Key}", $"Age row must be an inline table mapping target species to probability.");
                    var list = new List<(ISpecies, double)>();
                    double sum = 0.0;
                    foreach (var targetKv in map)
                    {
                        string targetName = targetKv.Key;
                        if (!double.TryParse(Convert.ToString(targetKv.Value), out double prob)) throw new InputValueException(Convert.ToString(targetKv.Value), $"Invalid probability for target '{targetName}' in [transition.data.{sourceName}.{ageKv.Key}].");
                        if (prob < 0.0 || prob > 1.0) throw new InputValueException(Convert.ToString(targetKv.Value), $"Probability for target '{targetName}' in [transition.data.{sourceName}.{ageKv.Key}] must be between 0.0 and 1.0.");
                        ISpecies targetSpecies;
                        if (string.Equals(targetName, "DEAD", StringComparison.OrdinalIgnoreCase)) targetSpecies = null;
                        else
                        {
                            if (!speciesNameToISpecies.TryGetValue(targetName, out targetSpecies)) throw new InputValueException(targetName, $"Species '{targetName}' in [transition.data.{sourceName}.{ageKv.Key}] does not exist.");
                            if (!speciesToGroup.TryGetValue(targetSpecies, out string targetGroup) || !string.Equals(targetGroup, groupName, StringComparison.OrdinalIgnoreCase)) throw new InputValueException(targetName, $"Target species '{targetName}' must belong to the same group as source '{sourceName}'.");
                        }
                        if (prob > 0.0) list.Add((targetSpecies, prob));
                        sum += prob;
                    }
                    double eps = 1e-9;
                    if (exhaustiveProbability)
                    {
                        if (Math.Abs(sum - 1.0) > eps) throw new InputValueException($"transition.data.{sourceName}.{ageKv.Key}", $"Probabilities must sum to exactly 1.0 when exhaustive_probability is true.");
                    }
                    else
                    {
                        if (sum - 1.0 > eps) throw new InputValueException($"transition.data.{sourceName}.{ageKv.Key}", $"Probabilities must sum to 1.0 or less.");
                    }
                    ageMatrix[ageKey] = list.ToArray();
                }

                var speciesMatrix = new SpeciesAgeMatrix(sourceSpecies, groupHealthy[groupName], ageMatrix, below, inRange, above);
                speciesMatrices[sourceSpecies] = speciesMatrix;
            }

            parameters.SpeciesTransitionAgeMatrix = speciesMatrices;

            if (parameters.DistanceDispersalDecayKernelFunction == null) throw new InputValueException("dispersal.kernel", "Failed to construct kernel.");

            PlugIn.ModelCore.UI.WriteLine("Configuration summary:");
            PlugIn.ModelCore.UI.WriteLine($"  Timestep: {parameters.Timestep}");
            PlugIn.ModelCore.UI.WriteLine($"  Transmission rate: {parameters.TransmissionRate}");
            PlugIn.ModelCore.UI.WriteLine($"  Species host index: {speciesHostIndexPath}");
            PlugIn.ModelCore.UI.WriteLine($"  Dispersal kernel: {parameters.DistanceDispersalDecayKernel}");
            PlugIn.ModelCore.UI.WriteLine($"  Dispersal maximum distance: {parameters.DispersalMaxDistance}");
            PlugIn.ModelCore.UI.WriteLine($"  SHI mode: {parameters.SHIMode}");
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

            PlugIn.ModelCore.UI.WriteLine("Transition configuration:");
            PlugIn.ModelCore.UI.WriteLine($"  Exhaustive probability: {exhaustiveProbability}");
            PlugIn.ModelCore.UI.WriteLine("  Groups:");
            foreach (var g in groupHealthy.Keys)
            {
                PlugIn.ModelCore.UI.WriteLine($"    {g}: healthy={groupHealthy[g].Name}, infected=[{string.Join(", ", groupInfected[g])}]");
            }
            PlugIn.ModelCore.UI.WriteLine("  Derived healthy species:");
            foreach (var hs in parameters.DerivedHealthySpecies) PlugIn.ModelCore.UI.WriteLine($"    {hs.Name}");
            PlugIn.ModelCore.UI.WriteLine("  Infected species:");
            foreach (var isx in parameters.InfectedSpeciesLookup) PlugIn.ModelCore.UI.WriteLine($"    {isx.Name}");
            PlugIn.ModelCore.UI.WriteLine("  Species age transition matrices:");
            foreach (var kvp in parameters.SpeciesTransitionAgeMatrix)
            {
                var sp = kvp.Key;
                var mat = kvp.Value;
                PlugIn.ModelCore.UI.WriteLine($"    source={sp.Name}, healthy={mat.DesignatedHealthySpecies().Name}");
            }

            foreach (var kvp in parameters.SpeciesTransitionAgeMatrix)
            {
                var sp = kvp.Key;
                var mat = kvp.Value;
                var dict = (Dictionary<ushort, (ISpecies, double)[]>)kvp.Value.GetType().GetField("_ageTransitionMatrix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(kvp.Value);
                var ages = new List<ushort>(dict.Keys);
                ages.Sort();
                foreach (var age in ages)
                {
                    var dist = dict[age];
                    var parts = new List<string>();
                    foreach (var t in dist)
                    {
                        parts.Add($"{(t.Item1 == null ? "DEAD" : t.Item1.Name)}={t.Item2}");
                    }
                    PlugIn.ModelCore.UI.WriteLine($"      age {age}: {string.Join(", ", parts)}");
                }
            }

            //Environment.Exit(1);
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
            if (text == null) throw new FormatException("Kernel is required.");
            string t = text.Trim();
            if (string.Equals(t, "negative_exponent", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.NegativeExponent;
            if (string.Equals(t, "power_law", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.PowerLaw;
            if (string.Equals(t, "anchored_power_law", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.SingleAnchoredPowerLaw;
            if (string.Equals(t, "double_anchored_power_law", StringComparison.OrdinalIgnoreCase)) return DistanceDispersalDecayKernel.DoubleAnchoredPowerLaw;
            throw new FormatException("Valid kernels: negative_exponent, power_law, anchored_power_law, double_anchored_power_law");
        }

        private static bool TryGetCaseInsensitive(IDictionary<string, object> table, string key, out object value)
        {
            foreach (var kv in table)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
            }
            value = null;
            return false;
        }

        

        


    }
}

