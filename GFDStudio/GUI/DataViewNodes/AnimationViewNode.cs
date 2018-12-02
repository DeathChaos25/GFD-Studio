using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Common;
using GFDLibrary.Models.Conversion;
using GFDStudio.FormatModules;
using GFDStudio.GUI.TypeConverters;

namespace GFDStudio.GUI.DataViewNodes
{
    public class AnimationViewNode : DataViewNode<Animation>
    {
        private VariantUserPropertyList mProperties;

        public override DataViewNodeMenuFlags ContextMenuFlags => 
            DataViewNodeMenuFlags.Delete | DataViewNodeMenuFlags.Export | DataViewNodeMenuFlags.Move | DataViewNodeMenuFlags.Replace;

        public override DataViewNodeFlags NodeFlags => DataViewNodeFlags.Leaf;

        public AnimationFlags Flags
        {
            get => Data.Flags;
            set => SetDataProperty( value );
        }

        public float Duration
        {
            get => Data.Duration;
            set => SetDataProperty( value );
        }

        public int ControllerCount => Data.Controllers.Count;

        //public List<AnimationController> Controllers { get; set; }

        //// 10
        //public List<AnimationFlag10000000DataEntry> Field10
        //{
        //    get => Model.Field10;
        //    set => SetDataProperty( value );
        //}

        //[Browsable(false)]
        //public AnimationExtraDataViewNode Field14 { get; set; }

        public BoundingBox? BoundingBox
        {
            get => Data.BoundingBox;
            set => SetDataProperty( value );
        }

        //public AnimationFlag80000000Data Field1C
        //{
        //    get => Model.Field1C;
        //    set => SetDataProperty( value );
        //}

        [Browsable( true )]
        [TypeConverter( typeof( ExpandableObjectConverter ) )]
        public VariantUserPropertyList Properties
        {
            get => mProperties;
            set
            {
                mProperties     = value;
                Data.Properties = new UserPropertyDictionary( Properties.Select( x => x.ToTypedUserProperty() ) );
            }
        }

        public float? Speed
        {
            get => Data.Speed;
            set => SetDataProperty( value );
        }

        protected internal AnimationViewNode( string text, Animation data ) : base( text, data )
        {
        }

        protected override void InitializeCore()
        {
            RegisterExportHandler<Animation>( path => Data.Save( path ) );
            RegisterReplaceHandler<Animation>( Resource.Load<Animation> );
            RegisterReplaceHandler<Assimp.Scene>( file =>
            {
                var animation = AnimationConverter.ConvertFromAssimpScene( file, new AnimationConverterOptions() );
                var modelViewModel = Parent?.Parent as ModelPackViewNode;

                if ( modelViewModel?.Model != null )
                {
                    animation.FixTargetIds( modelViewModel.Model.Data );
                }
                else
                {
                    ImportModelAndFixTargetIds( animation );
                }

                return animation;
            } );
            RegisterCustomHandler( "Fix IDs", () => ImportModelAndFixTargetIds( Data ) );
        }

        protected override void InitializeViewCore()
        {
            Properties = new VariantUserPropertyList( Data.Properties, () => Properties = mProperties );
        }

        private static void ImportModelAndFixTargetIds( Animation animation )
        {
            using ( var dialog = new OpenFileDialog() )
            {
                dialog.Filter = ModuleFilterGenerator.GenerateFilter( new[] { FormatModuleUsageFlags.Import }, typeof( ModelPack ) );
                dialog.AutoUpgradeEnabled = true;
                dialog.CheckPathExists = true;
                dialog.Title = "Select a model file.";
                dialog.ValidateNames = true;
                dialog.AddExtension = true;

                if ( dialog.ShowDialog() != DialogResult.OK )
                    return;

                try
                {
                    var model = Resource.Load<ModelPack>( dialog.FileName );
                    if ( model.Model != null )
                        animation.FixTargetIds( model.Model );
                }
                catch ( Exception e )
                {
                    Console.WriteLine( e );
                }
            }
        }
    }
}