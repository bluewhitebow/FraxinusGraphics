#if !UNITY_6000_0_OR_NEWER
using System;
using Unity.Collections;

namespace UnityEngine.Rendering.RenderGraphModule.NativeRenderPassCompiler
{
    // Unity 6 stores these descriptors in UnityEngine.Rendering and submits the
    // complete array through CommandBuffer.BeginRenderPass. Unity 2022 exposes
    // the same native render-pass capability through ScriptableRenderContext,
    // but does not expose the descriptor types. Keep an unmanaged equivalent
    // here so the Unity 6 compiler and merge algorithm can be reused unchanged.
    [Flags]
    internal enum SubPassFlags
    {
        None = 0,
        ReadOnlyDepth = 1 << 0,
        ReadOnlyStencil = 1 << 1,
    }

    internal struct AttachmentIndexArray
    {
        FixedAttachmentArray<int> m_Indices;

        internal AttachmentIndexArray(int length)
        {
            m_Indices = new FixedAttachmentArray<int>(length);
        }

        internal int Length => m_Indices.size;

        internal ref int this[int index] => ref m_Indices[index];
    }

    internal struct SubPassDescriptor
    {
        internal AttachmentIndexArray colorOutputs;
        internal AttachmentIndexArray inputs;
        internal SubPassFlags flags;
    }

    internal partial class NativePassCompiler
    {
        NativeList<int> m_Unity2022SubPassColors;
        NativeList<int> m_Unity2022SubPassInputs;
        bool m_Unity2022RenderPassActive;
        bool m_Unity2022SubPassActive;
        bool m_Unity2022FoveatedRenderingActive;

        void QueueUnity2022GraphicsFencePass(int passId)
        {
            // A single fence at the end of the native render pass covers every
            // raster producer in that pass.  Keep each producer id mapped to
            // the same fence because readers refer to the original graph pass.
            for (int i = 0; i < m_Unity2022PendingGraphicsFencePassIds.Count; ++i)
            {
                if (m_Unity2022PendingGraphicsFencePassIds[i] == passId)
                    return;
            }

            m_Unity2022PendingGraphicsFencePassIds.Add(passId);
        }

        void EmitUnity2022GraphicsFences(InternalRenderGraphContext rgContext)
        {
            if (m_Unity2022PendingGraphicsFencePassIds.Count == 0)
                return;

            if (rgContext.contextlessTesting)
            {
                m_Unity2022PendingGraphicsFencePassIds.Clear();
                return;
            }

            // EndRenderPass/EndSubPass has already been issued by the caller,
            // so this fence is ordered after tile stores/resolves as required by
            // an async queue consumer.
            var fence = rgContext.cmd.CreateAsyncGraphicsFence();
            for (int i = 0; i < m_Unity2022PendingGraphicsFencePassIds.Count; ++i)
                contextData.fences[m_Unity2022PendingGraphicsFencePassIds[i]] = fence;

            m_Unity2022PendingGraphicsFencePassIds.Clear();
        }

        void DisposeUnity2022NativeRenderPassBridge()
        {
            if (m_Unity2022SubPassColors.IsCreated)
                m_Unity2022SubPassColors.Dispose();
            if (m_Unity2022SubPassInputs.IsCreated)
                m_Unity2022SubPassInputs.Dispose();

            m_Unity2022RenderPassActive = false;
            m_Unity2022SubPassActive = false;
            m_Unity2022FoveatedRenderingActive = false;
        }

        void EnsureUnity2022SubPassArrays()
        {
            if (!m_Unity2022SubPassColors.IsCreated)
                m_Unity2022SubPassColors = new NativeList<int>(FixedAttachmentArray<int>.MaxAttachments, Allocator.Persistent);
            if (!m_Unity2022SubPassInputs.IsCreated)
                m_Unity2022SubPassInputs = new NativeList<int>(FixedAttachmentArray<int>.MaxAttachments, Allocator.Persistent);
        }

        void FillUnity2022SubPassArrays(in SubPassDescriptor descriptor)
        {
            EnsureUnity2022SubPassArrays();

            m_Unity2022SubPassColors.Resize(descriptor.colorOutputs.Length, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < descriptor.colorOutputs.Length; ++i)
                m_Unity2022SubPassColors[i] = descriptor.colorOutputs[i];

            m_Unity2022SubPassInputs.Resize(descriptor.inputs.Length, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < descriptor.inputs.Length; ++i)
                m_Unity2022SubPassInputs[i] = descriptor.inputs[i];
        }

        static void FlushUnity2022CommandBuffer(InternalRenderGraphContext rgContext)
        {
            // Contextless tests deliberately carry an invalid context. They
            // still exercise the complete compiler and pass callbacks, but no
            // immediate ScriptableRenderContext operation can be submitted.
            if (rgContext.contextlessTesting)
                return;

            // Unity 2022 exposes native render-pass transitions directly on
            // ScriptableRenderContext, so pending graph commands must be sent
            // before those transitions. Do not submit a buffer that is already
            // empty: ExecuteCommandBuffer still crosses the managed/native
            // boundary and can become visible in an otherwise empty frame.
            if (rgContext.cmd.sizeInBytes == 0)
                return;

            rgContext.renderContext.ExecuteCommandBuffer(rgContext.cmd);
            rgContext.cmd.Clear();
        }

        void BeginUnity2022SubPass(InternalRenderGraphContext rgContext, in SubPassDescriptor descriptor)
        {
            if (rgContext.contextlessTesting)
                return;

            FillUnity2022SubPassArrays(descriptor);

            var colors = m_Unity2022SubPassColors.AsArray();
            bool isDepthReadOnly = (descriptor.flags & SubPassFlags.ReadOnlyDepth) != 0;
            bool isStencilReadOnly = (descriptor.flags & SubPassFlags.ReadOnlyStencil) != 0;

            if (m_Unity2022SubPassInputs.Length > 0)
                rgContext.renderContext.BeginSubPass(colors, m_Unity2022SubPassInputs.AsArray(), isDepthReadOnly, isStencilReadOnly);
            else
                rgContext.renderContext.BeginSubPass(colors, isDepthReadOnly, isStencilReadOnly);

            m_Unity2022SubPassActive = true;
        }

        void ExecuteBeginRenderPassUnity2022(
            InternalRenderGraphContext rgContext,
            int width,
            int height,
            int volumeDepth,
            int samples,
            NativeArray<AttachmentDescriptor> attachments,
            int depthAttachmentIndex,
            ref NativePassData nativePass)
        {
            if (rgContext.contextlessTesting)
                return;

            // Resource initialization, waits and foveation state were recorded
            // before this call. ScriptableRenderContext render-pass operations
            // are immediate, so submit that prefix before opening the pass.
            FlushUnity2022CommandBuffer(rgContext);

            rgContext.renderContext.BeginRenderPass(
                width, height, volumeDepth, samples, attachments, depthAttachmentIndex);
            m_Unity2022RenderPassActive = true;

            ref var firstSubPass = ref contextData.nativeSubPassData.ElementAt(nativePass.firstNativeSubPass);
            BeginUnity2022SubPass(rgContext, firstSubPass);
        }

        void ExecuteNextSubPassUnity2022(InternalRenderGraphContext rgContext, ref PassData pass)
        {
            if (rgContext.contextlessTesting)
                return;

            if (!m_Unity2022RenderPassActive || !m_Unity2022SubPassActive)
                throw new InvalidOperationException("Cannot advance a Unity 2022 native subpass without an active render pass.");

            // All draws for the previous subpass must reach the context before
            // ending it. This is the ordering used by URP 14's native pass path.
            FlushUnity2022CommandBuffer(rgContext);
            rgContext.renderContext.EndSubPass();
            m_Unity2022SubPassActive = false;

            ref var nativePass = ref contextData.nativePassData.ElementAt(pass.nativePassIndex);
            ref var nextSubPass = ref contextData.nativeSubPassData.ElementAt(
                nativePass.firstNativeSubPass + pass.nativeSubPassIndex);
            BeginUnity2022SubPass(rgContext, nextSubPass);
        }

        void ExecuteEndRenderPassUnity2022(InternalRenderGraphContext rgContext)
        {
            if (rgContext.contextlessTesting)
                return;

            FlushUnity2022CommandBuffer(rgContext);

            if (m_Unity2022SubPassActive)
            {
                rgContext.renderContext.EndSubPass();
                m_Unity2022SubPassActive = false;
            }

            if (m_Unity2022RenderPassActive)
            {
                rgContext.renderContext.EndRenderPass();
                m_Unity2022RenderPassActive = false;
            }
        }

        void AbortUnity2022RenderPass(InternalRenderGraphContext rgContext)
        {
            if (rgContext.contextlessTesting)
                return;

            // Do not submit a partially recorded pass after a callback throws.
            // Closing the context is best-effort so a cleanup error never hides
            // the original RenderGraph exception.
            rgContext.cmd.Clear();

            if (m_Unity2022SubPassActive)
            {
                try
                {
                    rgContext.renderContext.EndSubPass();
                }
                catch (Exception)
                {
                }
                m_Unity2022SubPassActive = false;
            }

            if (m_Unity2022RenderPassActive)
            {
                try
                {
                    rgContext.renderContext.EndRenderPass();
                }
                catch (Exception)
                {
                }
                m_Unity2022RenderPassActive = false;
            }

            if (m_Unity2022FoveatedRenderingActive)
            {
                try
                {
                    rgContext.cmd.SetFoveatedRenderingMode(FoveatedRenderingMode.Disabled);
                }
                catch (Exception)
                {
                }
                m_Unity2022FoveatedRenderingActive = false;
            }
        }
    }
}
#endif
