using System;
using System.Collections.Generic;
using System.Linq;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// How Pawlygon VRCFT 1.x drives each Unified Expressions blendshape from its face tracking parameters, so a fix
    /// shape can follow the same values as the shapes it fixes. Taken from the 1.x FX controller's blend trees: each
    /// shape's weight (0 to 1) as a piecewise-linear function of one parameter, held at the ends. Its limiters and
    /// emulations (e.g. brows lowered by squinting) aren't included.
    /// </summary>
    internal static class FTFixesSources
    {
        internal enum Tracking { Eye, Lip }

        internal sealed class Source
        {
            public string Shape;
            /// <summary>Parameter name without the prefix, e.g. EyeLidLeft.</summary>
            public string Parameter;
            /// <summary>(parameter value, shape weight 0..1), sorted by parameter value.</summary>
            public (float Value, float Weight)[] Points;
            public Tracking Tracking;

            /// <summary>The shape's weight for a parameter value, held at the ends like a 1D blend tree.</summary>
            public float Evaluate(float value)
            {
                if (value <= Points[0].Value) return Points[0].Weight;
                for (int i = 1; i < Points.Length; i++)
                {
                    if (value <= Points[i].Value)
                    {
                        var (x0, y0) = Points[i - 1];
                        var (x1, y1) = Points[i];
                        return y0 + (y1 - y0) * (value - x0) / (x1 - x0);
                    }
                }
                return Points[Points.Length - 1].Weight;
            }
        }

        static readonly (float, float)[] Rising = { (0f, 0f), (1f, 1f) };
        static readonly (float, float)[] Falling = { (-1f, 1f), (0f, 0f) };

        static readonly Dictionary<string, Source> Sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Other names for the same shape (ARKit and the Unified Expressions base names).</summary>
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "EyeBlinkLeft", "EyeClosedLeft" }, { "EyeBlinkRight", "EyeClosedRight" },
            { "BrowLowererLeft", "BrowDownLeft" }, { "BrowLowererRight", "BrowDownRight" },
            { "MouthCornerPullLeft", "MouthSmileLeft" }, { "MouthCornerPullRight", "MouthSmileRight" },
            { "MouthClose", "MouthClosed" },
            { "MouthFunnel", "LipFunnel" }, { "MouthPucker", "LipPucker" },
            { "MouthRollUpper", "LipSuckUpper" }, { "MouthRollLower", "LipSuckLower" },
            { "MouthShrugUpper", "MouthRaiserUpper" }, { "MouthShrugLower", "MouthRaiserLower" },
        };

        static FTFixesSources()
        {
            foreach (var side in new[] { "Left", "Right" })
            {
                Add($"EyeClosed{side}", $"EyeLid{side}", Tracking.Eye, (0f, 1f), (0.75f, 0f));
                Add($"EyeWide{side}", $"EyeLid{side}", Tracking.Eye, (0.75f, 0f), (1f, 1f));
                Add($"EyeSquint{side}", $"EyeSquint{side}", Tracking.Eye, Rising);
                Add($"EyeLookUp{side}", "EyeY", Tracking.Eye, (0f, 0f), (0.6f, 1f));
                Add($"EyeLookDown{side}", "EyeY", Tracking.Eye, (-0.6f, 1f), (0f, 0f));
                Add($"BrowDown{side}", $"BrowExpression{side}", Tracking.Eye, Falling);
                Add($"BrowInnerUp{side}", $"BrowExpression{side}", Tracking.Eye, Rising);
                Add($"BrowOuterUp{side}", $"BrowExpression{side}", Tracking.Eye, Rising);

                Add($"MouthUpperUp{side}", $"MouthUpperUp{side}", Tracking.Lip, Rising);
                Add($"MouthLowerDown{side}", "MouthLowerDown", Tracking.Lip, Rising);
                Add($"MouthSmile{side}", $"SmileFrown{side}", Tracking.Lip, Rising);
                Add($"MouthFrown{side}", $"SmileFrown{side}", Tracking.Lip, Falling);
                Add($"CheekSquint{side}", $"CheekSquint{side}", Tracking.Lip, Rising);
                Add($"CheekPuff{side}", $"CheekPuffSuck{side}", Tracking.Lip, Rising);
                Add($"CheekSuck{side}", $"CheekPuffSuck{side}", Tracking.Lip, Falling);
                Add($"MouthStretch{side}", $"MouthTightenerStretch{side}", Tracking.Lip, Falling);
                Add($"MouthTightener{side}", $"MouthTightenerStretch{side}", Tracking.Lip, Rising);
                Add($"NoseSneer{side}", "NoseSneer", Tracking.Lip, Rising);
                Add($"MouthPress{side}", "MouthPress", Tracking.Lip, Rising);
                Add($"LipSuckUpper{side}", "LipSuckUpper", Tracking.Lip, Rising);
                Add($"LipSuckLower{side}", "LipSuckLower", Tracking.Lip, Rising);
                Add($"Jaw{side}", "JawX", Tracking.Lip, side == "Left" ? Falling : Rising);
                Add($"Mouth{side}", "MouthX", Tracking.Lip, side == "Left" ? Falling : Rising);
                Add($"Tongue{side}", "TongueX", Tracking.Lip, side == "Left" ? Falling : Rising);
            }
            Add("EyeLookInLeft", "EyeLeftX", Tracking.Eye, (0f, 0f), (0.6f, 1f));
            Add("EyeLookOutLeft", "EyeLeftX", Tracking.Eye, (-0.6f, 1f), (0f, 0f));
            Add("EyeLookInRight", "EyeRightX", Tracking.Eye, (-0.6f, 1f), (0f, 0f));
            Add("EyeLookOutRight", "EyeRightX", Tracking.Eye, (0f, 0f), (0.6f, 1f));

            Add("JawOpen", "JawOpen", Tracking.Lip, Rising);
            Add("JawForward", "JawForward", Tracking.Lip, Rising);
            Add("MouthClosed", "MouthClosed", Tracking.Lip, Rising);
            Add("MouthLowerDown", "MouthLowerDown", Tracking.Lip, Rising);
            Add("MouthRaiserUpper", "MouthRaiserUpper", Tracking.Lip, Rising);
            Add("MouthRaiserLower", "MouthRaiserLower", Tracking.Lip, Rising);
            Add("MouthPress", "MouthPress", Tracking.Lip, Rising);
            Add("NoseSneer", "NoseSneer", Tracking.Lip, Rising);
            Add("LipSuckUpper", "LipSuckUpper", Tracking.Lip, Rising);
            Add("LipSuckLower", "LipSuckLower", Tracking.Lip, Rising);
            Add("TongueOut", "TongueOut", Tracking.Lip, Rising);
            Add("TongueUp", "TongueY", Tracking.Lip, Rising);
            Add("TongueDown", "TongueY", Tracking.Lip, Falling);
            foreach (var shape in new[] { "LipFunnel", "LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight" })
            {
                Add(shape, "LipFunnel", Tracking.Lip, Rising);
            }
            foreach (var shape in new[] { "LipPucker", "LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight" })
            {
                Add(shape, "LipPucker", Tracking.Lip, Rising);
            }
        }

        static void Add(string shape, string parameter, Tracking tracking, params (float, float)[] points)
        {
            Sources[shape] = new Source { Shape = shape, Parameter = parameter, Points = points, Tracking = tracking };
        }

        /// <summary>How 1.x drives the blendshape named <paramref name="name"/> (any case, or an alias), or null.</summary>
        internal static Source Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (Aliases.TryGetValue(name, out var alias)) name = alias;
            return Sources.TryGetValue(name, out var source) ? source : null;
        }

        /// <summary>The parameters the sources read, for documentation and the parameter list.</summary>
        internal static IEnumerable<string> Parameters => Sources.Values.Select(s => s.Parameter).Distinct().OrderBy(p => p);
    }
}
