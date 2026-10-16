using System.Drawing;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace PrintGear;

public sealed partial class PrintSupportCommand
{
    private static double lastTrussBaseWidthMm;
    private static double lastTrussWidthMm = 10, lastTrussRodMm = .8, lastTrussHeadMm = 3;

    private static Result RunTrussMode(RhinoDoc doc, double nozzle, double printThickness, double scale)
    {
        var width = new OptionDouble(lastTrussWidthMm*scale,true,doc.ModelAbsoluteTolerance);
        var baseWidth = new OptionDouble(lastTrussBaseWidthMm*scale,true,0);
        var rod = new OptionDouble(lastTrussRodMm*scale,true,doc.ModelAbsoluteTolerance);
        var head = new OptionDouble(lastTrussHeadMm*scale,true,doc.ModelAbsoluteTolerance);
        using var options = new GetOption();
        options.SetCommandPrompt("第 3 層：設定桁架參數（斜撐固定 60°）；尺寸採文件單位，Enter 選取量體");
        options.AcceptNothing(true);
        options.AddOptionDouble(new LocalizeStringPair("TrussWidth","桁架寬度"),ref width);
        options.AddOptionDouble(new LocalizeStringPair("BaseWidth","底部桁架寬度"),ref baseWidth);
        options.AddOptionDouble(new LocalizeStringPair("RodDiameter","桿徑"),ref rod);
        options.AddOptionDouble(new LocalizeStringPair("HeadLength","尖端長度"),ref head);
        while (true)
        {
            var parameterResult=options.Get();
            if(parameterResult==GetResult.Cancel)return Result.Cancel;
            if(parameterResult==GetResult.Nothing)break;
            if(parameterResult!=GetResult.Option)return Result.Failure;
        }

        using var selection = new GetObject();
        selection.SetCommandPrompt("選取需要支撐的封閉量體（支撐底部對齊整個量體最低 Z）");
        selection.GeometryFilter = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion;
        selection.SubObjectSelect = false;
        selection.Get();
        if (selection.CommandResult() != Result.Success) return selection.CommandResult();
        var reference = selection.Object(0);
        using var target = reference.Brep()?.DuplicateBrep();
        if (target is null || !target.IsValid || !target.IsSolid)
        { RhinoApp.WriteLine("請選封閉多重曲面或封閉擠出量體。"); return Result.Failure; }
        if (target.SolidOrientation == BrepSolidOrientation.Inward) target.Flip();
        using var point = new GetPoint();
        point.SetCommandPrompt("在量體側面、下懸面或其邊線上點選支撐接觸點");
        if (!point.Constrain(target,-1,-1,false)) return Result.Failure;
        point.Get();
        if (point.CommandResult() != Result.Success) return point.CommandResult();
        var contact=point.Point();
        var reselectOption=options.AddOption(new LocalizeStringPair("ReselectPoint","重新選點"));
        options.SetCommandPrompt("預覽桁架支撐；斜撐固定 60°，可調整桁架參數，Enter 建立，Esc 取消");
        var preview = new TrussPreview();
        var cancelled = false;
        void Escape(object? sender,EventArgs args) => cancelled = true;
        RhinoApp.EscapeKeyPressed += Escape;
        try
        {
            while(true)
            {
                cancelled = false;
                preview.Enabled=false;
                preview.Solid?.Dispose(); preview.Solid=null;
                var p=new TrussSupportParameters(nozzle,printThickness,head.CurrentValue,width.CurrentValue,rod.CurrentValue,baseWidth.CurrentValue,3*scale);
                try
                {
                    preview.Solid=TrussSupportBuilder.BuildAtContact(target,contact,p,doc.ModelAbsoluteTolerance,out contact,() => {
                        RhinoApp.Wait(); if(cancelled)throw new OperationCanceledException();
                    });
                }
                catch(OperationCanceledException) { return Result.Cancel; }
                catch(Exception e) { RhinoApp.WriteLine("無有效預覽："+e.Message); }
                preview.Enabled=preview.Solid is not null;
                doc.Views.Redraw();
                var result=options.Get();
                if(result==GetResult.Cancel)return Result.Cancel;
                if(result==GetResult.Option)
                {
                    if(options.OptionIndex()==reselectOption)
                    {
                        preview.Enabled=false;doc.Views.Redraw();
                        point.Get();
                        if(point.CommandResult()!=Result.Success)return point.CommandResult();
                        contact=point.Point();
                    }
                    continue;
                }
                if(result!=GetResult.Nothing)return Result.Failure;
                if(preview.Solid is null)continue;
                using var attributes=new ObjectAttributes { Name="PrintSupport_Truss",LayerIndex=doc.Layers.CurrentLayerIndex };
                attributes.SetUserString("PrintSupport.Type","Truss");
                attributes.SetUserString("PrintSupport.TrussStyle","TriangularRoundSingleBrace");
                attributes.SetUserString("PrintSupport.BraceAngleDegrees","60");
                attributes.SetUserString("PrintSupport.BraceAngleReference","WorldXY");
                attributes.SetUserString("PrintSupport.TipMinAngleDegrees","60");
                attributes.SetUserString("PrintSupport.TopStrutMinAngleDegrees","60");
                attributes.SetUserString("PrintSupport.TipShape","HemisphereWithConicalStem");
                attributes.SetUserString("PrintSupport.TargetId",reference.ObjectId.ToString());
                void Put(string name,double value) => attributes.SetUserString("PrintSupport."+name,(value/scale).ToString("R",CultureInfo.InvariantCulture));
                Put("NozzleSizeMm",nozzle); Put("PrintThicknessMm",printThickness);
                Put("TipDiameterMm",nozzle);Put("TipPenetrationMm",nozzle*.05);
                Put("TrussWidthMm",p.Width);Put("RodDiameterMm",p.RodDiameter);Put("HeadLengthMm",p.HeadLength);
                Put("BasePlateThicknessMm",p.PrintThickness*3);Put("FootHeightMm",p.FootHeight);Put("FootBaseDiameterMm",p.RodDiameter*3);
                Put("BaseWidthInputMm",p.BaseWidth);Put("BaseWidthMm",p.EffectiveBaseWidth);
                Put("BaseZMm",target.GetBoundingBox(true).Min.Z);
                Put("ContactXMm",contact.X);Put("ContactYMm",contact.Y);Put("ContactZMm",contact.Z);
                if(doc.Objects.AddBrep(preview.Solid,attributes)==Guid.Empty)return Result.Failure;
                lastTrussBaseWidthMm=p.BaseWidth/scale;
                lastTrussWidthMm=p.Width/scale;lastTrussRodMm=p.RodDiameter/scale;lastTrussHeadMm=p.HeadLength/scale;
                RhinoApp.WriteLine("已建立封閉桁架支撐；底部對齊所選量體最低 Z，原量體保留。");
                return Result.Success;
            }
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= Escape;
            preview.Enabled=false;preview.Solid?.Dispose();doc.Views.Redraw();
        }
    }

    private sealed class TrussPreview : DisplayConduit
    {
        public Brep? Solid { get; set; }
        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        { if(Solid is not null)e.IncludeBoundingBox(Solid.GetBoundingBox(false)); }
        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if(Solid is null)return;
            using var material=new DisplayMaterial(Color.DarkOrange,.25);
            e.Display.DrawBrepShaded(Solid,material);e.Display.DrawBrepWires(Solid,Color.OrangeRed,1);
        }
    }
}
