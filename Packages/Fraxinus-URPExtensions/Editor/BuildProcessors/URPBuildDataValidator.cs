using System;
using System.Text;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    class URPBuildDataValidator
    {
        private static void ValidateRenderPipelineGlobalSettings(UniversalRenderPipelineGlobalSettings globalSettingsInstance, StringBuilder failures)
        {
            if (globalSettingsInstance == null)
                failures.AppendLine($"- The {nameof(UniversalRenderPipelineGlobalSettings)} of the project are missing.");
        }

        public static bool IsProjectValidForBuilding(BuildReport report, out string message)
        {
            using (GenericPool<StringBuilder>.Get(out var failures))
            {
                failures.Clear();

                ValidateRenderPipelineGlobalSettings(UniversalRenderPipelineGlobalSettings.Ensure(), failures);

                string allFailures = failures.ToString();

                if (!string.IsNullOrEmpty(allFailures))
                {
                    message =
                        $"Please fix the following errors before building:{Environment.NewLine}{allFailures}. {Environment.NewLine}";
                    return false;
                }
            }

            message = string.Empty;
            return true;
        }
    }
}
