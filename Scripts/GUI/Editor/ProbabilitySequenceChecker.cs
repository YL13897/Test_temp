using UnityEditor;
using UnityEngine;

// Temporary Editor-only validator for ExperimentBlockControl.randomSequence.
// Run in Edit Mode: open the experiment scene, then select
// Tools > Experiment > Check Probability Sequence and read the Unity Console.
// It checks total trials and the probability/case counts.

public static class ProbabilitySequenceChecker
{
    [MenuItem("Tools/Experiment/Check Probability Sequence")]
    static void Check()
    {
        var control = Object.FindFirstObjectByType<ExperimentBlockControl>();
        if (control == null)
        {
            Debug.LogError("[Probability] ExperimentBlockControl not found.");
            return;
        }

        // Read private serialized configuration without adding runtime accessors.
        var data = new SerializedObject(control);
        var probabilities = data.FindProperty("blockProbabilities");
        string sequence = data.FindProperty("randomSequence").stringValue;
        string cases = data.FindProperty("caseSequence").stringValue;
        int[] expectedCases =
        {
            data.FindProperty("leftLfCount").intValue,
            data.FindProperty("rightRfCount").intValue,
            data.FindProperty("leftRfCount").intValue,
            data.FindProperty("rightLfCount").intValue
        };
        int sections = expectedCases[0] + expectedCases[1] + expectedCases[2] + expectedCases[3];
        int[] counts = new int[probabilities.arraySize];
        int[,] caseCounts = new int[probabilities.arraySize, 4];

        if (cases.Length != sequence.Length)
        {
            Debug.LogError($"[Probability] Sequence lengths differ: probability={sequence.Length}, case={cases.Length}.");
            return;
        }

        // Count trials and cases for each probability
        for (int i = 0; i < sequence.Length; i++)
        {
            int probability = sequence[i] - '0'; // Convert char to int
            int caseIndex = cases[i] - '0';
            if (probability < 0 || probability >= counts.Length || caseIndex < 0 || caseIndex >= 4)
            {
                Debug.LogError($"[Probability] Invalid value at trial {i + 1}.");
                return;
            }
            counts[probability]++; // Increment the count for this probability
            caseCounts[probability, caseIndex]++; // Increment the count for this case at this probability
        }

        bool valid = sequence.Length == probabilities.arraySize * sections;
        for (int i = 0; i < counts.Length; i++)
        {
            valid &= counts[i] == sections; // Each probability should have the same number of trials as the total sections.
            for (int j = 0; j < expectedCases.Length; j++)
                valid &= caseCounts[i, j] == expectedCases[j];
            Debug.Log($"[Probability] {probabilities.GetArrayElementAtIndex(i).floatValue:P3}: {counts[i]} trials; cases " +
                $"{caseCounts[i, 0]}/{caseCounts[i, 1]}/{caseCounts[i, 2]}/{caseCounts[i, 3]}");
        }

        if (valid)
            Debug.Log($"[Probability] Sequence valid: {sequence.Length} trials.");
        else
            Debug.LogError($"[Probability] Sequence invalid: expected {probabilities.arraySize * sections} trials, found {sequence.Length}.");
    }
}
