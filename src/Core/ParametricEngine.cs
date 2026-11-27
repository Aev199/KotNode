using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    public sealed class MarkParameter
    {
        public string Name { get; set; }="";
        public string Label { get; set; }="";
        public double Value { get; set; }
        public string Formula { get; set; }="";
        public string Unit { get; set; }="мм";
    }
    public sealed class PartBinding
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public Guid PartId { get; set; }
        public string Property { get; set; }=""; // e.g. "Pipe.OuterDiameter", "Plate.Length", "CurvedPlate.AngleDeg"
        public string Formula { get; set; }="";
    }
    public sealed class VertexExpr
    {
        public string ExprX { get; set; }="0";
        public string ExprY { get; set; }="0";
    }
    public sealed class RoundHoleExpr
    {
        public string ExprCenterX { get; set; }="0";
        public string ExprCenterY { get; set; }="0";
        public string ExprRadius { get; set; }="10";
    }
    public sealed class PolygonBinding
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public Guid PartId { get; set; }
        public List<VertexExpr> Outer { get; set; }=new List<VertexExpr>();
        public List<RoundHoleExpr> RoundHoles { get; set; }=new List<RoundHoleExpr>();
    }
    public enum PlacementRuleKind { OffsetFromPart=0, OnPipeSurface=1, CircularArray=2, LinearArray=3 }
    public sealed class PlacementRule
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public PlacementRuleKind Kind { get; set; }
        public Guid TargetInstanceId { get; set; }
        public Guid ReferenceInstanceId { get; set; }
        public string ExprX { get; set; }="0";
        public string ExprY { get; set; }="0";
        public string ExprZ { get; set; }="0";
        public string ExprRotXDeg { get; set; }="0";
        public string ExprRotYDeg { get; set; }="0";
        public string ExprRotZDeg { get; set; }="0";
        public string ExprAngleDeg { get; set; }="0";
        public string ExprCount { get; set; }="1";
        public List<Guid> GeneratedInstanceIds { get; set; }=new List<Guid>();
    }
    public sealed class SolveReport
    {
        public Dictionary<string,double> Values { get; set; }=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
        public int UpdatedParts { get; set; }
        public int UpdatedInstances { get; set; }
        public List<Guid> AddedInstances { get; set; }=new List<Guid>();
        public List<Guid> RemovedInstances { get; set; }=new List<Guid>();
    }

    // Safe, deterministic recursive-descent formula evaluator (no reflection/scripts/eval).
    public static class FormulaEngine
    {
        sealed class Parser
        {
            readonly string src;
            readonly IDictionary<string,double> vars;
            int pos;
            public Parser(string s,IDictionary<string,double> v){src=s??"";vars=v;}
            void Skip(){while(pos<src.Length&&char.IsWhiteSpace(src[pos]))pos++;}
            bool Match(string tok)
            {
                Skip();
                if(pos+tok.Length<=src.Length&&string.CompareOrdinal(src,pos,tok,0,tok.Length)==0)
                {pos+=tok.Length;return true;}
                return false;
            }
            public double ParseAll()
            {
                double v=ParseComparison();
                Skip();
                if(pos<src.Length)throw new InvalidDataException("Лишний символ в формуле: '"+src.Substring(pos)+"'");
                if(!Guard.Finite(v))throw new InvalidDataException("Формула дала некорректное число (NaN/Infinity).");
                return v;
            }
            double ParseComparison()
            {
                double left=ParseAddSub();
                Skip();
                if(Match("<="))return left<=ParseAddSub()?1:0;
                if(Match(">="))return left>=ParseAddSub()?1:0;
                if(Match("=="))return Math.Abs(left-ParseAddSub())<1e-9?1:0;
                if(Match("!="))return Math.Abs(left-ParseAddSub())>=1e-9?1:0;
                if(Match("<"))return left<ParseAddSub()?1:0;
                if(Match(">"))return left>ParseAddSub()?1:0;
                return left;
            }
            double ParseAddSub()
            {
                double v=ParseMulDiv();
                while(true)
                {
                    if(Match("+"))v+=ParseMulDiv();
                    else if(Match("-"))v-=ParseMulDiv();
                    else return v;
                }
            }
            double ParseMulDiv()
            {
                double v=ParsePower();
                while(true)
                {
                    if(Match("*"))v*=ParsePower();
                    else if(Match("/"))
                    {
                        double d=ParsePower();
                        if(Math.Abs(d)<=1e-12)throw new InvalidDataException("Деление на ноль в формуле.");
                        v/=d;
                    }
                    else return v;
                }
            }
            double ParsePower()
            {
                double v=ParseUnary();
                if(Match("^"))v=Math.Pow(v,ParsePower());
                return v;
            }
            double ParseUnary()
            {
                if(Match("+"))return ParseUnary();
                if(Match("-"))return -ParseUnary();
                return ParsePrimary();
            }
            double ParsePrimary()
            {
                Skip();
                if(pos>=src.Length)throw new InvalidDataException("Неожиданный конец формулы.");
                if(Match("("))
                {
                    double v=ParseComparison();
                    if(!Match(")"))throw new InvalidDataException("Не закрыта скобка в формуле.");
                    return v;
                }
                if(char.IsDigit(src[pos])||src[pos]=='.')
                {
                    int start=pos;
                    while(pos<src.Length&&(char.IsDigit(src[pos])||src[pos]=='.'))pos++;
                    string num=src.Substring(start,pos-start);
                    if(!double.TryParse(num,NumberStyles.Float,CultureInfo.InvariantCulture,out var val))
                        throw new InvalidDataException("Неверное число в формуле: "+num);
                    return val;
                }
                if(char.IsLetter(src[pos])||src[pos]=='_')
                {
                    int start=pos;
                    while(pos<src.Length&&(char.IsLetterOrDigit(src[pos])||src[pos]=='_'))pos++;
                    string id=src.Substring(start,pos-start);
                    if(Match("("))
                    {
                        var args=new List<double>();
                        if(!Match(")"))
                        {
                            do{args.Add(ParseComparison());}while(Match(",")||Match(";"));
                            if(!Match(")"))throw new InvalidDataException("Не закрыта скобка функции "+id);
                        }
                        return EvalFunc(id,args);
                    }
                    if(string.Equals(id,"pi",StringComparison.OrdinalIgnoreCase))return Math.PI;
                    if(vars!=null&&vars.TryGetValue(id,out var found))return found;
                    throw new InvalidDataException("Неизвестный параметр в формуле: "+id);
                }
                throw new InvalidDataException("Недопустимый символ в формуле: '"+src[pos]+"'");
            }
            static double EvalFunc(string name,List<double> a)
            {
                switch(name.ToLowerInvariant())
                {
                    case "pi": return Math.PI;
                    case "abs": Need(a,1,name);return Math.Abs(a[0]);
                    case "sqrt": Need(a,1,name);if(a[0]<0)throw new InvalidDataException("Корень из отрицательного числа.");return Math.Sqrt(a[0]);
                    case "sin": Need(a,1,name);return Math.Sin(a[0]*Math.PI/180.0);
                    case "cos": Need(a,1,name);return Math.Cos(a[0]*Math.PI/180.0);
                    case "tan": Need(a,1,name);return Math.Tan(a[0]*Math.PI/180.0);
                    case "asin": Need(a,1,name);if(a[0]<-1||a[0]>1)throw new InvalidDataException("asin вне диапазона [-1; 1].");return Math.Asin(a[0])*180.0/Math.PI;
                    case "acos": Need(a,1,name);if(a[0]<-1||a[0]>1)throw new InvalidDataException("acos вне диапазона [-1; 1].");return Math.Acos(a[0])*180.0/Math.PI;
                    case "atan": Need(a,1,name);return Math.Atan(a[0])*180.0/Math.PI;
                    case "atan2": Need(a,2,name);return Math.Atan2(a[0],a[1])*180.0/Math.PI;
                    case "rad": Need(a,1,name);return a[0]*Math.PI/180.0;
                    case "deg": Need(a,1,name);return a[0]*180.0/Math.PI;
                    case "floor": Need(a,1,name);return Math.Floor(a[0]);
                    case "ceil": Need(a,1,name);return Math.Ceiling(a[0]);
                    case "round": Need(a,1,name);return Math.Round(a[0]);
                    case "min": Need(a,2,name);return Math.Min(a[0],a[1]);
                    case "max": Need(a,2,name);return Math.Max(a[0],a[1]);
                    case "if": Need(a,3,name);return Math.Abs(a[0])>1e-9?a[1]:a[2];
                    default: throw new InvalidDataException("Неизвестная функция в формуле: "+name);
                }
            }
            static void Need(List<double> a,int n,string fn){if(a.Count!=n)throw new InvalidDataException("Функция "+fn+" требует "+n+" аргумент(ов).");}
        }

        public static double Evaluate(string expression,IDictionary<string,double> variables)
            =>new Parser(expression,variables).ParseAll();
        public static HashSet<string> ReferencedIdentifiers(string expression)
        {
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(string.IsNullOrWhiteSpace(expression))return ids;
            int i=0;
            while(i<expression.Length)
            {
                if(char.IsLetter(expression[i])||expression[i]=='_')
                {
                    int start=i;
                    while(i<expression.Length&&(char.IsLetterOrDigit(expression[i])||expression[i]=='_'))i++;
                    string word=expression.Substring(start,i-start);
                    int j=i;while(j<expression.Length&&char.IsWhiteSpace(expression[j]))j++;
                    if(!(j<expression.Length&&expression[j]=='(')&&!string.Equals(word,"pi",StringComparison.OrdinalIgnoreCase))
                        ids.Add(word);
                }
                else i++;
            }
            return ids;
        }
    }

    public static class ParametricEngine
    {
        public static Dictionary<string,double> EvaluateParameters(IList<MarkParameter> parameters)
        {
            var values=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            if(parameters==null||parameters.Count==0)return values;
            var byName=new Dictionary<string,MarkParameter>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in parameters)
            {
                Guard.Require(p!=null&&!string.IsNullOrWhiteSpace(p.Name),"Задайте имя параметра.");
                string name=p.Name.Trim();
                Guard.Require(byName.ContainsKey(name)==false,"Дублирующийся параметр: "+name);
                byName[name]=p;
            }
            var state=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); // 1=visiting, 2=done
            Action<string> eval=null;
            eval=name=>{
                if(state.TryGetValue(name,out var st))
                {
                    if(st==2)return;
                    throw new InvalidDataException("Обнаружена циклическая зависимость параметров: "+name);
                }
                state[name]=1;
                var param=byName[name];
                if(string.IsNullOrWhiteSpace(param.Formula))
                {
                    Guard.Require(Guard.Finite(param.Value),"Неверное значение параметра: "+name);
                    values[name]=param.Value;
                }
                else
                {
                    foreach(var dep in FormulaEngine.ReferencedIdentifiers(param.Formula))
                        if(byName.ContainsKey(dep))eval(dep);
                    double v=FormulaEngine.Evaluate(param.Formula,values);
                    param.Value=v;
                    values[name]=v;
                }
                state[name]=2;
            };
            foreach(var name in byName.Keys)eval(name);
            return values;
        }

        static void ApplyBinding(PartDefinition part,string prop,double val)
        {
            if(prop!=null&&prop.StartsWith("EndCuts.",StringComparison.Ordinal))
            {
                Guard.Require(part.Kind==PartKind.Pipe||part.Kind==PartKind.IBeam,"Подрезки доступны только для труб и двутавров.");
                var bits=prop.Split('.');
                Guard.Require(bits.Length==3&&(bits[1]=="Start"||bits[1]=="End")&&(bits[2]=="AngleXDeg"||bits[2]=="AngleYDeg"||bits[2]=="OffsetMm"),"Неизвестное свойство подрезки.");
                if(part.EndCuts==null)part.EndCuts=new MemberEndTreatment();
                var cut=bits[1]=="Start"?part.EndCuts.Start:part.EndCuts.End;
                if(cut==null){cut=new PlanarEndCut();if(bits[1]=="Start")part.EndCuts.Start=cut;else part.EndCuts.End=cut;}
                if(bits[2]=="AngleXDeg")cut.AngleXDeg=val;
                else if(bits[2]=="AngleYDeg")cut.AngleYDeg=val;
                else cut.OffsetMm=val;
                return;
            }
            switch(prop)
            {
                case "Pipe.OuterDiameter": Guard.Require(part.Kind==PartKind.Pipe,"Привязка Pipe.* только для трубы.");part.Pipe.OuterDiameter=val;break;
                case "Pipe.Thickness": Guard.Require(part.Kind==PartKind.Pipe,"Привязка Pipe.* только для трубы.");part.Pipe.Thickness=val;break;
                case "Pipe.Length": Guard.Require(part.Kind==PartKind.Pipe,"Привязка Pipe.* только для трубы.");part.Pipe.Length=val;break;
                case "Plate.Length": Guard.Require(part.Kind==PartKind.RectangularPlate,"Привязка Plate.* только для прямоугольного листа.");part.Plate.Length=val;break;
                case "Plate.Width": Guard.Require(part.Kind==PartKind.RectangularPlate,"Привязка Plate.* только для прямоугольного листа.");part.Plate.Width=val;break;
                case "Plate.Thickness": Guard.Require(part.Kind==PartKind.RectangularPlate,"Привязка Plate.* только для прямоугольного листа.");part.Plate.Thickness=val;break;
                case "PolygonPlate.Thickness": Guard.Require(part.Kind==PartKind.PolygonPlate,"Привязка PolygonPlate.* только для контурного листа.");part.PolygonPlate.Thickness=val;break;
                case "CurvedPlate.InnerRadius": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.InnerRadius=val;break;
                case "CurvedPlate.Thickness": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.Thickness=val;break;
                case "CurvedPlate.Length": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.Length=val;break;
                case "CurvedPlate.AngleDeg": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.AngleRadians=val*Math.PI/180.0;break;
                case "IBeam.Length": Guard.Require(part.Kind==PartKind.IBeam,"Привязка IBeam.* только для двутавра.");part.IBeam.Length=val;break;
                case "Rebar.Diameter": Guard.Require(part.Kind==PartKind.Rebar,"Привязка Rebar.* только для арматуры.");part.Rebar.Diameter=val;break;
                default: throw new InvalidDataException("Неизвестное свойство привязки: "+prop);
            }
        }
        static Vector3 RotEuler(Vector3 v,double rx,double ry,double rz)
        {
            double cx=Math.Cos(rx),sx=Math.Sin(rx);
            double y1=v.Y*cx-v.Z*sx,z1=v.Y*sx+v.Z*cx,x1=v.X;
            double cy=Math.Cos(ry),sy=Math.Sin(ry);
            double x2=x1*cy+z1*sy,z2=-x1*sy+z1*cy,y2=y1;
            double cz=Math.Cos(rz),sz=Math.Sin(rz);
            return new Vector3(x2*cz-y2*sz,x2*sz+y2*cz,z2);
        }
        static RigidPlacement RelativeEuler(double dx,double dy,double dz,double rxDeg,double ryDeg,double rzDeg)
        {
            double rx=rxDeg*Math.PI/180.0,ry=ryDeg*Math.PI/180.0,rz=rzDeg*Math.PI/180.0;
            return new RigidPlacement
            {
                Origin=new Vector3(dx,dy,dz),
                AxisX=RotEuler(new Vector3(1,0,0),rx,ry,rz),
                AxisY=RotEuler(new Vector3(0,1,0),rx,ry,rz)
            };
        }
        static double EvalOrZero(string expr,IDictionary<string,double> vars)
            =>string.IsNullOrWhiteSpace(expr)?0:FormulaEngine.Evaluate(expr,vars);
        static RigidPlacement RotateAroundAxis(RigidPlacement p,RigidPlacement axisFrame,double angleRad)
        {
            var local=p.RelativeTo(axisFrame);
            double c=Math.Cos(angleRad),s=Math.Sin(angleRad);
            Func<Vector3,Vector3> rot=v=>new Vector3(v.X*c-v.Y*s,v.X*s+v.Y*c,v.Z);
            var rotatedLocal=new RigidPlacement{Origin=rot(local.Origin),AxisX=rot(local.AxisX),AxisY=rot(local.AxisY)};
            return rotatedLocal.InParent(axisFrame);
        }

        // Placement dependencies are separate from scalar formula dependencies. Arrays
        // produce instances too, so references to their children must wait for the array.
        static List<PlacementRule> OrderRules(AssemblyMark mark)
        {
            var rules=mark.PlacementRules??new List<PlacementRule>();
            var placements=new Dictionary<Guid,PlacementRule>();
            var generated=new Dictionary<Guid,PlacementRule>();
            var ids=new HashSet<Guid>();
            foreach(var r in rules)
            {
                Guard.Require(r!=null&&r.Id!=Guid.Empty&&ids.Add(r.Id)&&Enum.IsDefined(typeof(PlacementRuleKind),r.Kind),"Повреждено или повторено правило положения.");
                Guard.Require(mark.Instances.Any(i=>i.Id==r.TargetInstanceId),"Целевой экземпляр правила положения отсутствует.");
                Guard.Require(r.ReferenceInstanceId==Guid.Empty||mark.Instances.Any(i=>i.Id==r.ReferenceInstanceId),"Опорный экземпляр правила положения отсутствует.");
                if(r.Kind==PlacementRuleKind.CircularArray||r.Kind==PlacementRuleKind.LinearArray)
                {
                    foreach(var id in r.GeneratedInstanceIds??new List<Guid>())
                    {
                        Guard.Require(id!=r.TargetInstanceId&&!generated.ContainsKey(id),"Экземпляр принадлежит нескольким массивам или совпадает с их источником.");
                        generated[id]=r;
                    }
                }
                else
                {
                    Guard.Require(!placements.ContainsKey(r.TargetInstanceId),"Несколько правил задают положение одного экземпляра.");
                    placements[r.TargetInstanceId]=r;
                }
            }
            foreach(var id in placements.Keys)
                Guard.Require(!generated.ContainsKey(id),"Положение элемента массива задаётся массивом, а не отдельным правилом.");
            var ordered=new List<PlacementRule>();
            var state=new Dictionary<Guid,int>();
            Action<PlacementRule> visit=null;
            visit=r=>{
                if(state.TryGetValue(r.Id,out var st))
                {
                    Guard.Require(st==2,"Обнаружена циклическая зависимость правил положения.");
                    return;
                }
                state[r.Id]=1;
                if(r.ReferenceInstanceId!=Guid.Empty)
                {
                    if(placements.TryGetValue(r.ReferenceInstanceId,out var parent))visit(parent);
                    if(generated.TryGetValue(r.ReferenceInstanceId,out var array))visit(array);
                }
                if(r.Kind==PlacementRuleKind.CircularArray||r.Kind==PlacementRuleKind.LinearArray)
                {
                    if(placements.TryGetValue(r.TargetInstanceId,out var source))visit(source);
                    if(generated.TryGetValue(r.TargetInstanceId,out var array))visit(array);
                }
                state[r.Id]=2;ordered.Add(r);
            };
            foreach(var r in rules)visit(r);
            return ordered;
        }

        public static SolveReport Solve(AssemblyMark mark)
        {
            Guard.Require(mark!=null,"Марка не задана.");
            var draft=Json.Copy(mark);
            // Formula coordinates belong to the family, not its location on a sheet.
            var origin=draft.LayoutOrigin??new Vector3();
            foreach(var inst in draft.Instances)
                inst.Placement.Origin=new Vector3(inst.Placement.Origin.X-origin.X,inst.Placement.Origin.Y-origin.Y,inst.Placement.Origin.Z);
            var report=new SolveReport();
            report.Values=EvaluateParameters(draft.Parameters);

            if(draft.Bindings!=null)
            {
                foreach(var b in draft.Bindings)
                {
                    var part=draft.Parts.FirstOrDefault(p=>p.Id==b.PartId);
                    Guard.Require(part!=null,"Привязка ссылается на отсутствующую деталь.");
                    double v=FormulaEngine.Evaluate(b.Formula,report.Values);
                    ApplyBinding(part,b.Property,v);
                    report.UpdatedParts++;
                }
            }

            if(draft.PolygonBindings!=null)
            {
                foreach(var pb in draft.PolygonBindings)
                {
                    var part=draft.Parts.FirstOrDefault(p=>p.Id==pb.PartId);
                    Guard.Require(part!=null,"Привязка ссылается на отсутствующую деталь.");
                    Guard.Require(part.Kind==PartKind.PolygonPlate,"Привязка контура PolygonBinding допустима только для контурного листа.");
                    if(pb.Outer!=null&&pb.Outer.Count>=3)
                    {
                        part.PolygonPlate.Outer=pb.Outer.Select(v=>new Point2(
                            FormulaEngine.Evaluate(v.ExprX,report.Values),
                            FormulaEngine.Evaluate(v.ExprY,report.Values))).ToList();
                    }
                    if(pb.RoundHoles!=null&&pb.RoundHoles.Count>0)
                    {
                        part.PolygonPlate.RoundHoles=pb.RoundHoles.Select(h=>new RoundHole
                        {
                            Center=new Point2(FormulaEngine.Evaluate(h.ExprCenterX,report.Values),FormulaEngine.Evaluate(h.ExprCenterY,report.Values)),
                            Radius=FormulaEngine.Evaluate(h.ExprRadius,report.Values)
                        }).ToList();
                    }
                    report.UpdatedParts++;
                }
            }

            // Validate final dimensions together, never transient combinations.
            foreach(var part in draft.Parts)part.Validate();

            if(draft.PlacementRules!=null)
            {
                foreach(var rule in OrderRules(draft))
                {
                    var target=draft.Instances.FirstOrDefault(i=>i.Id==rule.TargetInstanceId);
                    Guard.Require(target!=null,"Целевой экземпляр правила удалён при перестроении массива.");
                    var refInst=rule.ReferenceInstanceId!=Guid.Empty?draft.Instances.FirstOrDefault(i=>i.Id==rule.ReferenceInstanceId):null;
                    Guard.Require(rule.ReferenceInstanceId==Guid.Empty||refInst!=null,"Опорный экземпляр удалён при перестроении массива.");
                    var refFrame=refInst!=null?refInst.Placement:new RigidPlacement();
                    if(rule.Kind==PlacementRuleKind.OffsetFromPart)
                    {
                        double dx=EvalOrZero(rule.ExprX,report.Values);
                        double dy=EvalOrZero(rule.ExprY,report.Values);
                        double dz=EvalOrZero(rule.ExprZ,report.Values);
                        double rx=EvalOrZero(rule.ExprRotXDeg,report.Values);
                        double ry=EvalOrZero(rule.ExprRotYDeg,report.Values);
                        double rz=EvalOrZero(rule.ExprRotZDeg,report.Values)+EvalOrZero(rule.ExprAngleDeg,report.Values);
                        var rel=RelativeEuler(dx,dy,dz,rx,ry,rz);
                        target.Placement=rel.InParent(refFrame);
                        report.UpdatedInstances++;
                    }
                    else if(rule.Kind==PlacementRuleKind.OnPipeSurface)
                    {
                        Guard.Require(refInst!=null,"Для OnPipeSurface укажите экземпляр трубы.");
                        var pipePart=draft.Parts.Single(p=>p.Id==refInst.PartId);
                        Guard.Require(pipePart.Kind==PartKind.Pipe,"Опорная деталь для OnPipeSurface должна быть трубой.");
                        double radius=pipePart.Pipe.OuterDiameter/2.0;
                        double radialOff=EvalOrZero(rule.ExprX,report.Values);
                        double axialZ=EvalOrZero(rule.ExprZ,report.Values);
                        double angDeg=EvalOrZero(rule.ExprAngleDeg,report.Values)+EvalOrZero(rule.ExprRotZDeg,report.Values);
                        double rx=EvalOrZero(rule.ExprRotXDeg,report.Values);
                        double ry=EvalOrZero(rule.ExprRotYDeg,report.Values);
                        double r=radius+radialOff;
                        double rad=angDeg*Math.PI/180.0;
                        double tangentOff=EvalOrZero(rule.ExprY,report.Values);
                        var rel=RelativeEuler(r*Math.Cos(rad)-tangentOff*Math.Sin(rad),r*Math.Sin(rad)+tangentOff*Math.Cos(rad),axialZ,rx,ry,angDeg);
                        target.Placement=rel.InParent(refFrame);
                        report.UpdatedInstances++;
                    }
                    else if(rule.Kind==PlacementRuleKind.CircularArray||rule.Kind==PlacementRuleKind.LinearArray)
                    {
                        int totalCount=(int)Math.Round(FormulaEngine.Evaluate(rule.ExprCount,report.Values));
                        Guard.Require(totalCount>=1&&totalCount<=200,"Число элементов массива должно быть от 1 до 200.");
                        int extra=totalCount-1;
                        if(rule.GeneratedInstanceIds==null)rule.GeneratedInstanceIds=new List<Guid>();
                        rule.GeneratedInstanceIds.RemoveAll(id=>!draft.Instances.Any(i=>i.Id==id));
                        while(rule.GeneratedInstanceIds.Count>extra)
                        {
                            var remId=rule.GeneratedInstanceIds[rule.GeneratedInstanceIds.Count-1];
                            rule.GeneratedInstanceIds.RemoveAt(rule.GeneratedInstanceIds.Count-1);
                            draft.Instances.RemoveAll(i=>i.Id==remId);
                            report.RemovedInstances.Add(remId);
                        }
                        while(rule.GeneratedInstanceIds.Count<extra)
                        {
                            var copy=new PartInstance{Id=Guid.NewGuid(),PartId=target.PartId,Placement=Json.Copy(target.Placement)};
                            draft.Instances.Add(copy);
                            rule.GeneratedInstanceIds.Add(copy.Id);
                            report.AddedInstances.Add(copy.Id);
                        }
                        double sx=rule.Kind==PlacementRuleKind.LinearArray?EvalOrZero(rule.ExprX,report.Values):0;
                        double sy=rule.Kind==PlacementRuleKind.LinearArray?EvalOrZero(rule.ExprY,report.Values):0;
                        double sz=rule.Kind==PlacementRuleKind.LinearArray?EvalOrZero(rule.ExprZ,report.Values):0;
                        // Express linear step in reference part frame (or WCS if none) so rotated beams step cleanly along their axis.
                        var refZ=refFrame.AxisX.Cross(refFrame.AxisY);
                        var worldStep=new Vector3(
                            refFrame.AxisX.X*sx+refFrame.AxisY.X*sy+refZ.X*sz,
                            refFrame.AxisX.Y*sx+refFrame.AxisY.Y*sy+refZ.Y*sz,
                            refFrame.AxisX.Z*sx+refFrame.AxisY.Z*sy+refZ.Z*sz);
                        double stepAng=0;
                        if(rule.Kind==PlacementRuleKind.CircularArray)
                        {
                            double degrees=EvalOrZero(rule.ExprAngleDeg,report.Values)+EvalOrZero(rule.ExprRotZDeg,report.Values);
                            stepAng=Math.Abs(degrees)<1e-12?2*Math.PI/totalCount:degrees*Math.PI/180.0;
                        }
                        for(int k=0;k<extra;k++)
                        {
                            var child=draft.Instances.Single(i=>i.Id==rule.GeneratedInstanceIds[k]);
                            child.PartId=target.PartId;
                            if(rule.Kind==PlacementRuleKind.CircularArray)
                                child.Placement=RotateAroundAxis(target.Placement,refFrame,stepAng*(k+1));
                            else
                            {
                                var o=target.Placement.Origin;
                                child.Placement=new RigidPlacement
                                {
                                    Origin=new Vector3(o.X+worldStep.X*(k+1),o.Y+worldStep.Y*(k+1),o.Z+worldStep.Z*(k+1)),
                                    AxisX=Json.Copy(target.Placement.AxisX),
                                    AxisY=Json.Copy(target.Placement.AxisY)
                                };
                            }
                            report.UpdatedInstances++;
                        }
                    }
                }
            }

            foreach(var inst in draft.Instances)inst.Placement.Origin=NodePlacement.Add(inst.Placement.Origin,origin);
            if(draft.Parts.Any(MemberEndCuts.HasCuts))draft.Schema=Math.Max(draft.Schema,3);
            draft.Validate();
            // Commit validated draft back into mark
            mark.Schema=draft.Schema;
            mark.Parts=draft.Parts;
            mark.Instances=draft.Instances;
            mark.Parameters=draft.Parameters;
            mark.Bindings=draft.Bindings;
            mark.PolygonBindings=draft.PolygonBindings;
            mark.PlacementRules=draft.PlacementRules;
            return report;
        }
    }
}
