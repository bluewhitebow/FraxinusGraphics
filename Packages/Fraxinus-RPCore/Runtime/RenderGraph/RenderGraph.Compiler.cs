using UnityEngine.Rendering.RenderGraphModule.NativeRenderPassCompiler;

namespace UnityEngine.Rendering.RenderGraphModule
{
    public partial class RenderGraph
    {
        // TODO(ddebaets) move old compile func/members over.
        NativePassCompiler nativeCompiler;

        internal NativePassCompiler CompileNativeRenderGraph(int graphHash)
        {
            using (new ProfilingScope(m_RenderGraphContext.cmd, ProfilingSampler.Get(RenderGraphProfileId.CompileRenderGraph)))
            {
                if (nativeCompiler == null)
                    nativeCompiler = new NativePassCompiler(m_CompilationCache);

                bool compilationIsCached = nativeCompiler.Initialize(
                    m_Resources,
                    m_RenderPasses,
                    m_DebugParameters.disablePassCulling,
                    name,
                    m_EnableCompilationCaching,
                    graphHash,
                    m_ExecutionCount);
                if (!compilationIsCached)
                    nativeCompiler.Compile(m_Resources);

                var passData = nativeCompiler.contextData.passData;
                int numPasses = passData.Length;
                for (int i = 0; i < numPasses; ++i)
                {
                    if (passData.ElementAt(i).culled)
                        continue;
                    var renderPass = m_RenderPasses[i];
                    m_RendererLists.AddRange(renderPass.usedRendererListList);
                }

                m_Resources.CreateRendererLists(
                    m_RendererLists,
                    m_RenderGraphContext.renderContext,
                    m_RendererListCulling);
                return nativeCompiler;
            }
        }

        void ExecuteNativeRenderGraph()
        {
            using (new ProfilingScope(m_RenderGraphContext.cmd, ProfilingSampler.Get(RenderGraphProfileId.ExecuteRenderGraph)))
            {
                try
                {
                    nativeCompiler.ExecuteGraph(m_RenderGraphContext, m_Resources, m_RenderPasses);

                    // Unity 2022 still incurs a managed/native submission
                    // cost for an empty command buffer. Native execution can
                    // legitimately produce no commands (for example an empty
                    // graph after pass culling), so avoid crossing that
                    // boundary when there is nothing to submit.
                    if (!m_RenderGraphContext.contextlessTesting &&
                        m_RenderGraphContext.cmd.sizeInBytes > 0)
                        m_RenderGraphContext.renderContext.ExecuteCommandBuffer(m_RenderGraphContext.cmd);

                    m_RenderGraphContext.cmd.Clear();
                }
                finally
                {
                    // Native execution owns its temporary async command
                    // buffers, but graph-level exception cleanup owns pooled
                    // resources. Export the latest completion fence even when
                    // ExecuteGraph throws so those resources remain
                    // quarantined until every submitted queue has completed.
                    if (nativeCompiler.TryGetLastSubmittedAsyncFence(out var completionFence))
                    {
                        m_LastSubmittedAsyncFence = completionFence;
                        m_HasSubmittedAsyncFence = true;
                    }

                    m_HasUnfencedAsyncWork = nativeCompiler.HasUnfencedAsyncWork;
                }
            }
        }
    }
}
