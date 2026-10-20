using System;
using System.Collections.Generic;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>How the shapes a fix fixes are combined into the fix shape's weight.</summary>
    public enum FTFixesCombine
    {
        Average,
        Minimum,
        Multiply,
        Maximum,
    }

    [Serializable]
    public class FTFixesManualFix
    {
        [Tooltip("The fix blendshape that gets driven.")]
        public string blendshape = "";

        [Tooltip("The blendshapes it fixes, separated by commas, e.g. MouthSmileLeft, EyeClosedLeft.")]
        public string sources = "";
    }

    /// <summary>
    /// Per-avatar Blendshape Fixes settings, saved next to the generated controller and prefab.
    /// </summary>
    public class FTFixesProfile : ScriptableObject
    {
        [Tooltip("Drive every blendshape named after the face tracking shapes it fixes, joined with '-' (e.g. MouthSmileLeft-EyeClosedLeft).")]
        public bool autoDetect = true;

        [Tooltip("How the fixed shapes are combined into the fix shape's weight.")]
        public FTFixesCombine combine = FTFixesCombine.Average;

        [Tooltip("Detected fixes left undriven, by blendshape name.")]
        public List<string> excludedFixes = new List<string>();

        [Tooltip("Extra fixes. An entry with the same blendshape as a detected fix replaces it.")]
        public List<FTFixesManualFix> fixes = new List<FTFixesManualFix>();

        [Tooltip("Prefix of the VRCFT parameters the fixes read. The OSCmooth proxies are synced and smoothed, like the Pawlygon VRCFT 1.x animator uses.")]
        public string parameterPrefix = "OSCm/Proxy/FT/v2/";

        [Tooltip("Fixes of eye shapes only run while this is on.")]
        public string eyeTrackingActive = "EyeTrackingActive";

        [Tooltip("Fixes of mouth shapes only run while this is on.")]
        public string lipTrackingActive = "LipTrackingActive";

        /// <summary>The plan the last generation was made from, to tell when it's out of date.</summary>
        [HideInInspector] public string lastGeneratedSignature;
    }
}
