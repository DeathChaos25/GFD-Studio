using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Effects;
using GFDLibrary.Materials;
using GFDLibrary.Models;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace GFDLibrary.Rendering.OpenGL
{
    public class GLModel : IDisposable
    {
        public ModelPack ModelPack { get; }

        public List<GLNode> Nodes { get; }

        public Dictionary<string, GLBaseMaterial> Materials { get; }

        public Animation Animation { get; private set; }

        public Animation BlendAnimation { get; private set; }

        private List<AnimationController> mMorphControllers = new List<AnimationController>();

        private List<AnimationController> mMaterialControllers = new List<AnimationController>();

        private Dictionary<(string, int), float> mMorphWeights = new Dictionary<(string, int), float>();

        public GLModel( ModelPack modelPack, MaterialTextureCreator textureCreator )
        {
            ModelPack = modelPack;

            var nodes = modelPack.Model.Nodes.ToList();
            Nodes = nodes.Select( x => new GLNode( x ) ).ToList();

            Materials = modelPack.Materials?.ToDictionary( x => x.Key, y => GLBaseMaterial.CreateGLMaterial( y.Value, textureCreator ) ) ??
                        new Dictionary<string, GLBaseMaterial>();

            foreach ( var glNode in Nodes )
            {
                glNode.Parent = Nodes.FirstOrDefault( x => x.Node == glNode.Node.Parent );
                if ( !glNode.Node.HasAttachments )
                    continue;

                foreach ( var attachment in glNode.Node.Attachments )
                {
                    if ( attachment.Type != NodeAttachmentType.Mesh )
                        continue;

                    var glMesh = new GLMesh( attachment.GetValue<Mesh>(), glNode.Node.WorldTransform, modelPack.Model.Bones, Nodes, Materials, version: modelPack.Version );
                    glNode.Meshes.Add( glMesh );
                }
            }
        }

        public void AddEplNodes( Epl epl, GLNode parentGlNode )
        {
            Trace.WriteLine( $"[GLModel.AddEplNodes] EPL RootNode='{epl.RootNode.Name}', Children={epl.RootNode.ChildCount}, HasAttachments={epl.RootNode.HasAttachments}, AttachPt='{parentGlNode.Node.Name}'" );

            foreach ( var child in epl.RootNode.Children )
            {
                AddEplNodeRecursive( child, parentGlNode );
            }

            // Also process attachments on the root node itself
            if ( epl.RootNode.HasAttachments )
            {
                ProcessEplNodeAttachments( epl.RootNode, parentGlNode );
            }
        }

        private void AddEplNodeRecursive( Node eplNode, GLNode parentGlNode )
        {
            var glNode = new GLNode( eplNode );
            glNode.Parent = parentGlNode;

            // Recalculate world transform based on the GLNode parent chain (not the EPL Node tree)
            var transform = Matrix4x4.CreateFromQuaternion( glNode.Node.Rotation ) * Matrix4x4.CreateScale( glNode.Node.Scale );
            transform.Translation = glNode.Node.Translation;
            glNode.CurrentTransform = transform;
            glNode.WorldTransform = glNode.CurrentTransform * glNode.Parent.WorldTransform;

            Nodes.Add( glNode );

            Trace.WriteLine( $"[GLModel] EPL node '{eplNode.Name}' parented to '{parentGlNode.Node.Name}', Attachments={eplNode.AttachmentCount}, Children={eplNode.ChildCount}, WorldPos={glNode.WorldTransform.Translation}" );

            ProcessEplNodeAttachments( eplNode, glNode );

            foreach ( var child in eplNode.Children )
            {
                AddEplNodeRecursive( child, glNode );
            }
        }

        private void ProcessEplNodeAttachments( Node eplNode, GLNode glNode )
        {
            if ( !eplNode.HasAttachments )
                return;

            foreach ( var attachment in eplNode.Attachments )
            {
                switch ( attachment.Type )
                {
                    case NodeAttachmentType.Mesh:
                    {
                        var glMesh = new GLMesh( attachment.GetValue<Mesh>(), glNode.WorldTransform, ModelPack.Model.Bones, Nodes, Materials, version: ModelPack.Version );
                        glNode.Meshes.Add( glMesh );
                        break;
                    }
                    case NodeAttachmentType.Epl:
                    {
                        AddEplNodes( attachment.GetValue<Epl>(), glNode );
                        break;
                    }
                    case NodeAttachmentType.EplLeaf:
                    {
                        AddEplLeafModel( attachment.GetValue<EplLeaf>(), glNode );
                        break;
                    }
                }
            }
        }

        public void AddEplLeafNodes( EplLeaf eplLeaf, GLNode glNode )
        {
            AddEplLeafModel( eplLeaf, glNode );
        }

        private void AddEplLeafModel( EplLeaf eplLeaf, GLNode attachmentGlNode )
        {
            var embeddedModelPack = TryLoadModelPackFromEplLeaf( eplLeaf );
            if ( embeddedModelPack == null )
                return;

            Trace.WriteLine( $"[GLModel.AddEplLeafModel] Loaded embedded ModelPack, Nodes={embeddedModelPack.Model.Nodes.Count()}, Materials={embeddedModelPack.Materials?.Count ?? 0}, Textures={embeddedModelPack.Textures?.Count ?? 0}" );

            // Merge embedded materials into main Materials dictionary
            if ( embeddedModelPack.Materials != null && embeddedModelPack.Textures != null )
            {
                foreach ( var kvp in embeddedModelPack.Materials )
                {
                    if ( !Materials.ContainsKey( kvp.Key ) )
                    {
                        var glMaterial = GLBaseMaterial.CreateGLMaterial( kvp.Value, ( material, textureName ) =>
                        {
                            if ( embeddedModelPack.Textures.TryGetTexture( textureName, out var texture ) )
                                return new GLTexture( texture );
                            return null;
                        } );
                        Materials[kvp.Key] = glMaterial;
                        Trace.WriteLine( $"[GLModel.AddEplLeafModel] Added embedded material '{kvp.Key}'" );
                    }
                }
            }

            // Walk the embedded model's node tree and create GLNodes + GLMeshes
            var embeddedRootNode = embeddedModelPack.Model.RootNode;
            if ( embeddedRootNode.HasAttachments )
                AddEmbeddedNodeAttachments( embeddedRootNode, attachmentGlNode, embeddedModelPack );

            foreach ( var child in embeddedRootNode.Children )
            {
                AddEmbeddedNodeRecursive( child, attachmentGlNode, embeddedModelPack );
            }
        }

        private void AddEmbeddedNodeRecursive( Node embeddedNode, GLNode parentGlNode, ModelPack embeddedModelPack )
        {
            var glNode = new GLNode( embeddedNode );
            glNode.Parent = parentGlNode;

            var transform = Matrix4x4.CreateFromQuaternion( glNode.Node.Rotation ) * Matrix4x4.CreateScale( glNode.Node.Scale );
            transform.Translation = glNode.Node.Translation;
            glNode.CurrentTransform = transform;
            glNode.WorldTransform = glNode.CurrentTransform * glNode.Parent.WorldTransform;

            Nodes.Add( glNode );

            AddEmbeddedNodeAttachments( embeddedNode, glNode, embeddedModelPack );

            foreach ( var child in embeddedNode.Children )
            {
                AddEmbeddedNodeRecursive( child, glNode, embeddedModelPack );
            }
        }

        private void AddEmbeddedNodeAttachments( Node embeddedNode, GLNode glNode, ModelPack embeddedModelPack )
        {
            if ( !embeddedNode.HasAttachments )
                return;

            foreach ( var attachment in embeddedNode.Attachments )
            {
                if ( attachment.Type == NodeAttachmentType.Mesh )
                {
                    var mesh = attachment.GetValue<Mesh>();
                    Trace.WriteLine( $"[GLModel] Embedded mesh on node '{embeddedNode.Name}': Material='{mesh.MaterialName}', Vertices={mesh.VertexCount}, Triangles={mesh.TriangleCount}" );
                    var glMesh = new GLMesh( mesh, glNode.WorldTransform, embeddedModelPack.Model.Bones, Nodes, Materials, version: embeddedModelPack.Version );
                    glNode.Meshes.Add( glMesh );
                }
            }
        }

        private static ModelPack TryLoadModelPackFromEplLeaf( EplLeaf eplLeaf )
        {
            if ( eplLeaf.Data is not EplModel eplModel )
            {
                Trace.WriteLine( $"[GLModel.TryLoadModelPackFromEplLeaf] EplLeaf.Data is not EplModel, it's {eplLeaf.Data?.GetType().Name}" );
                return null;
            }

            Trace.WriteLine( $"[GLModel.TryLoadModelPackFromEplLeaf] EplModel: HasEmbeddedFile={eplModel.HasEmbeddedFile}, EmbeddedFile.FileName='{eplModel.EmbeddedFile?.FileName}', DataLen={eplModel.EmbeddedFile?.DataLength}" );

            if ( eplModel.HasEmbeddedFile != 1 || eplModel.EmbeddedFile?.Data == null || eplModel.EmbeddedFile.Data.Length == 0 )
                return null;

            try
            {
                using var stream = new System.IO.MemoryStream( eplModel.EmbeddedFile.Data );
                return Resource.Load<ModelPack>( stream );
            }
            catch ( Exception ex )
            {
                Trace.WriteLine( $"[GLModel.TryLoadModelPackFromEplLeaf] Failed to parse as ModelPack: {ex.Message}" );
                return null;
            }
        }

        public void LoadAnimation( Animation animation )
        {
            Animation = animation;

            foreach ( var glNode in Nodes )
            {
                glNode.Controllers.Clear();
                glNode.Controllers.AddRange( animation.Controllers.Where( x => x.TargetKind == TargetKind.Node &&
                                                                               x.TargetName == glNode.Node.Name ) );
            }

            mMorphControllers.Clear();
            mMorphControllers.AddRange( animation.Controllers.Where( x => x.TargetKind == TargetKind.Morph ||
                                                                           x.TargetKind == TargetKind.MorphIndexed ) );

            mMaterialControllers.Clear();
            mMaterialControllers.AddRange( animation.Controllers.Where( x => x.TargetKind == TargetKind.Material ) );
        }

        public void LoadBlendAnimation( Animation animation )
        {
            BlendAnimation = animation;

            foreach ( var glNode in Nodes )
            {
                glNode.BlendControllers.Clear();
                glNode.BlendControllers.AddRange( animation.Controllers.Where( x => x.TargetKind == TargetKind.Node &&
                                                                                    x.TargetName == glNode.Node.Name ) );
            }
        }

        public void UnloadAnimation()
        {
            Animation = null;
            BlendAnimation = null;

            foreach ( var glNode in Nodes )
            {
                glNode.BlendControllers.Clear();
                // Calculate current transform
                var transform = Matrix4x4.CreateFromQuaternion( glNode.Node.Rotation ) * Matrix4x4.CreateScale( glNode.Node.Scale );
                transform.Translation   = glNode.Node.Translation;
                glNode.CurrentTransform = transform;

                // Calculate world transform
                glNode.WorldTransform =
                    glNode.Parent == null ? glNode.CurrentTransform : glNode.CurrentTransform * glNode.Parent.WorldTransform;
            }

            foreach ( var glNode in Nodes )
            {
                // Rebuild meshes
                for ( var i = 0; i < glNode.Meshes.Count; i++ )
                {
                    var oldGlMesh = glNode.Meshes[i];
                    if (oldGlMesh.Mesh != null)
                    {
                        glNode.Meshes[i] = new GLMesh( oldGlMesh.Mesh, glNode.WorldTransform, ModelPack.Model.Bones, Nodes, Materials, version: ModelPack.Version );
                    }

                    oldGlMesh.Dispose();
                }
            }

            mMorphControllers.Clear();
            mMorphWeights.Clear();

            mMaterialControllers.Clear();
            foreach ( var material in Materials.Values )
            {
                material.AnimatedAlpha = 1.0f;
                material.UVOffset = System.Numerics.Vector2.Zero;
                material.UVScale = System.Numerics.Vector2.One;
                material.UVRotation = 0f;
                material.UV1Offset = System.Numerics.Vector2.Zero;
                material.UV1Scale = System.Numerics.Vector2.One;
                material.UV1Rotation = 0f;
            }
        }

        private GLShaderProgram GetTargetShader(ShaderRegistry shaderRegistry, GLMesh glMesh, Matrix4 view, Matrix4 projection, HashSet<ResourceType> shaderPrograms )
        {
            if ( typeof( GLMetaphorMaterial ).IsInstanceOfType( glMesh.Material )
                && shaderRegistry.mMetaphorShaders.TryGetValue( ( (GLMetaphorMaterial)glMesh.Material ).ParameterSet.ResourceType, out var metaphorShader ) )
            {
                if ( !shaderPrograms.Contains( ((GLMetaphorMaterial)glMesh.Material).ParameterSet.ResourceType ) )
                {
                    metaphorShader.Use();
                    metaphorShader.SetUniform( "uView", view );
                    metaphorShader.SetUniform( "uProjection", projection );
                }
                return metaphorShader;
            }
            return shaderRegistry.mDefaultShader;
        }

        private bool ShouldMeshShowAsSelected( DrawContext context, GLMesh glMesh )
        {
            return context.SelectedMesh == glMesh.Mesh || context.SelectedMaterial?.Name == glMesh.Mesh.MaterialName;
        }

        public void Draw( DrawContext context )
        {
            if ( Animation != null || BlendAnimation != null )
                AnimateNodes( context.AnimationTime );
            context.ShaderRegistry.mDefaultShader.Use();
            context.ShaderRegistry.mDefaultShader.SetUniform( "uView", context.Camera.View );
            context.ShaderRegistry.mDefaultShader.SetUniform( "uProjection", context.Camera.Projection );

            HashSet<ResourceType> shaderProgramsInUse = new();

            // List to hold transparent meshes and their world transforms
            List<Tuple<GLMesh, Matrix4, GLShaderProgram>> transparentMeshes = new();

            // Draw opaque objects first
            foreach ( var glNode in Nodes )
            {
                if ( !glNode.IsVisible )
                    continue;

                for ( var i = 0; i < glNode.Meshes.Count; i++ )
                {
                    var glMesh = glNode.Meshes[i];

                    if ( ( Animation != null || BlendAnimation != null ) && glMesh.Mesh != null )
                    {
                        var oldGlMesh = glMesh;
                        var morphWeights = GetMorphWeightsForNode( glNode.Node );
                        if ( morphWeights.Count == 0 )
                            morphWeights = null;
                        glMesh = glNode.Meshes[i] = new GLMesh( oldGlMesh.Mesh, glNode.WorldTransform, ModelPack.Model.Bones, Nodes, Materials, morphWeights, ModelPack.Version );
                        oldGlMesh.Dispose();
                    }
                    GLShaderProgram targetShader = GetTargetShader( context.ShaderRegistry, glMesh, context.Camera.View, context.Camera.Projection, shaderProgramsInUse );
                    if ( !glMesh.Material.IsMaterialTransparent() ) // If opaque
                    {
                        if ( glMesh.Mesh != null )
                            targetShader.SetUniform( "uIsSelected", ShouldMeshShowAsSelected(context, glMesh ) );
                        glMesh.Draw( glNode.WorldTransform.ToOpenTK(), targetShader );
                    }
                    else // If transparent
                    {
                        transparentMeshes.Add( Tuple.Create( glMesh, glNode.WorldTransform.ToOpenTK(), targetShader ) );
                    }
                }
            }

            // Enable blending for transparent objects
            GL.Enable( EnableCap.Blend );

            // Sort transparent objects based on their distance from the camera
            transparentMeshes.Sort( ( a, b ) => OpenTK.Vector3.Distance( context.Camera.Translation, b.Item2.ExtractTranslation() ).CompareTo( OpenTK.Vector3.Distance( context.Camera.Translation, a.Item2.ExtractTranslation() ) ) );

            // Disable depth mask
            GL.DepthMask( false );

            // Then draw transparent objects
            foreach ( (var glMesh, var worldTransform, var shaderProgram) in transparentMeshes )
            {
                switch ( glMesh.Material.DrawMethod )
                {
                    case 1:
                        GL.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha );
                        break;
                    case 2:
                        GL.BlendFuncSeparate( BlendingFactorSrc.SrcAlpha, BlendingFactorDest.One, // RGB blending
                                              BlendingFactorSrc.Zero, BlendingFactorDest.One ); // Alpha blending
                        break;
                    case 4:
                        GL.BlendFunc( BlendingFactor.DstColor, BlendingFactor.Zero );
                        break;
                }

                if ( glMesh.Mesh != null )
                    shaderProgram.SetUniform( "uIsSelected", ShouldMeshShowAsSelected( context, glMesh ) );
                glMesh.Draw( worldTransform, shaderProgram );
            }

            // Re-enable depth mask
            GL.DepthMask( true );

            // Disable blending after drawing transparent objects
            GL.Disable( EnableCap.Blend );
        }


        private void AnimateNodes( double animationTime )
        {
            foreach ( var glNode in Nodes )
            {
                var rotation    = glNode.Node.Rotation;
                var translation = glNode.Node.Translation;
                var scale       = glNode.Node.Scale;

                foreach ( var controller in glNode.Controllers )
                {
                    foreach ( var layer in controller.Layers )
                    {
                        Key curKey = null;
                        Key nextKey = null;

                        if ( controller != null )
                        {
                            (curKey, nextKey) = GetCurrentAndNextKeys( layer, animationTime );
                        }

                        if ( controller != null && curKey != null )
                        {
                            if ( layer.HasPRSKeyFrames )
                            {
                                var prsKey = ( PRSKey )curKey;
                                var nextPrsKey = ( PRSKey )nextKey;

                                if ( nextPrsKey != null )
                                {
                                    InterpolateKeys( animationTime, layer, ref rotation, ref translation, ref scale, prsKey, nextPrsKey, Animation.Duration );
                                }
                                else
                                {
                                    if ( prsKey.HasRotation )
                                    {
                                        rotation = prsKey.Rotation;
                                    }

                                    if ( prsKey.HasPosition )
                                        translation = prsKey.Position * layer.PositionScale;

                                    if ( prsKey.HasScale )
                                        scale = prsKey.Scale * layer.ScaleScale;
                                }
                            }
                            else
                            {
                                //Debugger.Break();
                            }
                        }
                    }
                }

                foreach ( var blendController in glNode.BlendControllers )
                {
                    foreach ( var layer in blendController.Layers )
                    {
                        Key curKey = null;
                        Key nextKey = null;

                        var blendDuration = BlendAnimation.Duration;

                        // When animationTime exceeds the blend animation's duration, hold the
                        // last keyframe rather than wrapping around and interpolating garbage.
                        if ( blendDuration > 0 && animationTime >= blendDuration )
                        {
                            // Find the last key in the layer and apply it directly (no interpolation)
                            curKey = layer.Keys.LastOrDefault();
                        }
                        else
                        {
                            (curKey, nextKey) = GetCurrentAndNextKeys( layer, animationTime );
                        }

                        if ( curKey != null && layer.HasPRSKeyFrames )
                        {
                            var prsKey = ( PRSKey )curKey;
                            var nextPrsKey = ( PRSKey )nextKey;

                            if ( nextPrsKey != null )
                            {
                                var offsetRotation = Quaternion.Identity;
                                var offsetTranslation = Vector3.Zero;
                                var offsetScale = Vector3.Zero;
                                InterpolateKeys( animationTime, layer, ref offsetRotation, ref offsetTranslation, ref offsetScale, prsKey, nextPrsKey, BlendAnimation.Duration );
                                translation += offsetTranslation;
                                rotation = Quaternion.Concatenate( offsetRotation, rotation );
                                scale += offsetScale;

                            }
                            else
                            {
                                if ( prsKey.HasPosition )
                                    translation += prsKey.Position * layer.PositionScale;

                                if ( prsKey.HasRotation )
                                    rotation = Quaternion.Concatenate( prsKey.Rotation, rotation );

                                if ( prsKey.HasScale )
                                    scale += prsKey.Scale * layer.ScaleScale;

                            }
                        }
                    }
                }

                // Calculate current transform
                var transform = Matrix4x4.CreateFromQuaternion( rotation ) * Matrix4x4.CreateScale( scale );
                transform.Translation   = translation;
                glNode.CurrentTransform = transform;

                // Calculate world transform
                glNode.WorldTransform =
                    glNode.Parent == null ? glNode.CurrentTransform : glNode.CurrentTransform * glNode.Parent.WorldTransform;

            }

            // Evaluate morph target weights
            mMorphWeights.Clear();
            foreach ( var controller in mMorphControllers )
            {
                float weight = 0f;

                foreach ( var layer in controller.Layers )
                {
                    if ( !layer.HasSingleKeyFrames )
                        continue;

                    var (curKey, nextKey) = GetCurrentAndNextKeys( layer, animationTime );

                    if ( curKey is SingleKey singleKey )
                    {
                        weight = singleKey.Value;

                        if ( nextKey is SingleKey nextSingleKey )
                        {
                            var nextTime = nextSingleKey.Time < singleKey.Time
                                ? ( nextSingleKey.Time + Animation.Duration )
                                : nextSingleKey.Time;

                            if ( nextTime > singleKey.Time )
                            {
                                var blend = ( float )( ( animationTime - singleKey.Time ) / ( nextTime - singleKey.Time ) );
                                weight = singleKey.Value + ( nextSingleKey.Value - singleKey.Value ) * blend;
                            }
                        }
                    }
                }

                mMorphWeights[( controller.TargetName, controller.TargetId )] = weight;
            }

            // check for material animations
            foreach ( var controller in mMaterialControllers )
            {
                if ( !Materials.TryGetValue( controller.TargetName, out var material ) )
                    continue;

                foreach ( var layer in controller.Layers )
                {
                    if ( layer.HasSingleKeyFrames && layer.KeyType == KeyType.EKeyType_Opacity )
                    {
                        // check Diffusivity flag (bit 6) for alpha animation
                        if ( ( material.MatFlags & ( 1 << 6 ) ) == 0 )
                            continue;

                        var (curKey, nextKey) = GetCurrentAndNextKeys( layer, animationTime );

                        if ( curKey is SingleKey singleKey )
                        {
                            float alpha = singleKey.Value;

                            if ( nextKey is SingleKey nextSingleKey )
                            {
                                var nextTime = nextSingleKey.Time < singleKey.Time
                                    ? ( nextSingleKey.Time + Animation.Duration )
                                    : nextSingleKey.Time;

                                if ( nextTime > singleKey.Time )
                                {
                                    var blend = ( float )( ( animationTime - singleKey.Time ) / ( nextTime - singleKey.Time ) );
                                    alpha = singleKey.Value + ( nextSingleKey.Value - singleKey.Value ) * blend;
                                }
                            }

                            material.AnimatedAlpha = alpha;
                        }
                    }
                    else if ( layer.HasSingle5KeyFrames )
                    {
                        //  check HasUVAnimation flag (bit 7) for UV Animations
                        if ( ( material.MatFlags & ( 1 << 7 ) ) == 0 )
                            continue;

                        var (curKey, nextKey) = GetCurrentAndNextKeys( layer, animationTime );

                        if ( curKey is Single5Key single5Key )
                        {
                            float offsetX = single5Key.UVOffsetX;
                            float offsetY = single5Key.UVOffsetY;
                            float scaleX  = single5Key.UVScaleX;
                            float scaleY  = single5Key.UVScaleY;
                            float rot     = single5Key.UVRotation;

                            // EKeyType_UV0Transform (13) and EKeyType_UV1Transform (20) use interpolation
                            if ( ( layer.KeyType == KeyType.EKeyType_UV1Transform || layer.KeyType == KeyType.EKeyType_UV0Transform ) && nextKey is Single5Key nextSingle5Key )
                            {
                                var nextTime = nextSingle5Key.Time < single5Key.Time
                                    ? ( nextSingle5Key.Time + Animation.Duration )
                                    : nextSingle5Key.Time;

                                if ( nextTime > single5Key.Time )
                                {
                                    var blend = ( float )( ( animationTime - single5Key.Time ) / ( nextTime - single5Key.Time ) );
                                    offsetX = single5Key.UVOffsetX + ( nextSingle5Key.UVOffsetX - single5Key.UVOffsetX ) * blend;
                                    offsetY = single5Key.UVOffsetY + ( nextSingle5Key.UVOffsetY - single5Key.UVOffsetY ) * blend;
                                    scaleX  = single5Key.UVScaleX  + ( nextSingle5Key.UVScaleX  - single5Key.UVScaleX  ) * blend;
                                    scaleY  = single5Key.UVScaleY  + ( nextSingle5Key.UVScaleY  - single5Key.UVScaleY  ) * blend;
                                    rot     = single5Key.UVRotation + ( nextSingle5Key.UVRotation - single5Key.UVRotation ) * blend;
                                }
                            }

                            // EKeyType_UV0Transform (13) and EKeyType_UV0Transform_NoInterp (21) target UV0; EKeyType_UV1Transform (20) and EKeyType_UV1Transform_NoInterp (36) target UV1
                            if ( layer.KeyType == KeyType.EKeyType_UV1Transform || layer.KeyType == KeyType.EKeyType_UV1Transform_NoInterp )
                            {
                                material.UV1Offset   = new System.Numerics.Vector2( offsetX, offsetY );
                                material.UV1Scale    = new System.Numerics.Vector2( scaleX, scaleY );
                                material.UV1Rotation = rot;
                            }
                            else
                            {
                                material.UVOffset   = new System.Numerics.Vector2( offsetX, offsetY );
                                material.UVScale    = new System.Numerics.Vector2( scaleX, scaleY );
                                material.UVRotation = rot;
                            }
                        }
                    }
                }
            }
        }

        private void InterpolateKeys( double animationTime, AnimationLayer layer, ref Quaternion rotation, ref Vector3 translation, ref Vector3 scale, PRSKey prsKey, PRSKey nextPrsKey, float duration )
        {
            var nextTime = ( nextPrsKey.Time < prsKey.Time
                ? ( nextPrsKey.Time + duration )
                : nextPrsKey.Time );

            var segmentLength = nextTime - prsKey.Time;
            if ( segmentLength <= 0 )
            {
                // Degenerate segment — just use the current key's values directly
                if ( prsKey.HasRotation )
                    rotation = prsKey.Rotation;

                if ( prsKey.HasPosition )
                    translation = prsKey.Position * layer.PositionScale;

                if ( prsKey.HasScale )
                    scale = prsKey.Scale * layer.ScaleScale;

                return;
            }

            var blend = ( float ) ( ( animationTime - prsKey.Time ) / segmentLength );

            if ( prsKey.HasRotation )
                rotation = Quaternion.Slerp( prsKey.Rotation, nextPrsKey.Rotation, blend );

            if ( prsKey.HasPosition )
            {
                translation = Vector3.Lerp( prsKey.Position * layer.PositionScale,
                                            nextPrsKey.Position * layer.PositionScale,
                                            blend );
            }

            if ( prsKey.HasScale )
            {
                scale = Vector3.Lerp( prsKey.Scale * layer.ScaleScale,
                                      nextPrsKey.Scale * layer.ScaleScale,
                                      blend );
            }
        }

        private Dictionary<int, float> GetMorphWeightsForNode( Node node )
        {
            var result = new Dictionary<int, float>();
            foreach ( var kvp in mMorphWeights )
            {
                var targetName = kvp.Key.Item1;
                if ( IsMorphTargetMatch( node.Name, targetName ) )
                {
                    result[kvp.Key.Item2] = kvp.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Checks whether a node name matches a morph controller target name, accounting for
        /// atlus trolling where the controller name differs from the model's node name
        /// </summary>
        private static bool IsMorphTargetMatch( string nodeName, string targetName )
        {
            if ( nodeName == targetName || nodeName.StartsWith( targetName + "_" ) )
                return true;

            // Known Dancing game body part aliases
            switch ( targetName )
            {
                case "m_face00":
                case "m_face":
                case "head_00":
                    return nodeName == "m_face00" || nodeName == "m_face" || nodeName == "head_00" || nodeName == "m_face_00";

                case "m_face01":
                case "m_mouth":
                case "head_01":
                    return nodeName == "m_face01" || nodeName == "m_mouth" || nodeName == "head_01" || nodeName == "m_face_01";

                case "m_Leye":
                case "L_eye":
                    return nodeName == "m_Leye" || nodeName == "L_eye";

                case "m_Reye":
                case "R_eye":
                    return nodeName == "m_Reye" || nodeName == "R_eye";

                case "m_mayu00":
                case "m_mayuge":
                case "m_blow":
                    return nodeName == "m_mayu00" || nodeName == "m_mayuge" || nodeName == "m_blow";
            }

            return false;
        }

        private static (Key curKey, Key nextKey) GetCurrentAndNextKeys( AnimationLayer layer, double animationTime )
        {
            Key curKey = null;
            Key nextKey = null;

            // Find most recent key
            foreach ( var key in layer.Keys )
            {
                if ( key.Time <= animationTime )
                    curKey = key;
            }

            // Find next key
            if ( curKey != null )
            {
                foreach ( var key in layer.Keys )
                {
                    if ( key != curKey && key.Time != curKey.Time && key.Time >= animationTime )
                    {
                        nextKey = key;
                        break;
                    }
                }

                if ( nextKey == null )
                {
                    nextKey = layer.Keys.FirstOrDefault( x => x.Time != curKey.Time );
                }
            }

            return ( curKey, nextKey );
        }

        #region IDisposable Support
        private bool mDisposed; // To detect redundant calls

        protected virtual void Dispose( bool disposing )
        {
            if ( !mDisposed )
            {
                if ( disposing )
                {
                    GLVertexArray.UnbindAll();
                    GL.BindBuffer( BufferTarget.ArrayBuffer, 0 );
                    GL.BindBuffer( BufferTarget.ElementArrayBuffer, 0 );
                    GL.BindTexture( TextureTarget.Texture2D, 0 );

                    foreach ( var glNode in Nodes )
                    {
                        glNode.Dispose();

                        foreach ( var geometry in glNode.Meshes )
                            geometry.Dispose();
                    }
                }

                mDisposed = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose( true );
        }
        #endregion
    }

    public class DrawContext
    {
        public ShaderRegistry ShaderRegistry { get; init; }
        public GLCamera Camera { get; init; }
        public double AnimationTime { get; init; }
        public Mesh SelectedMesh { get; init; }
        public Material SelectedMaterial { get; init; }
    }
}
