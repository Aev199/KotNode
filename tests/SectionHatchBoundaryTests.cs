// Tests the unchanged production boundary factory with a small API double.
// It verifies explicit closure and exact curve type, not AutoCAD hatch rendering.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KotNode.Core;
using KotNode.AutoCAD;

namespace Autodesk.AutoCAD.Geometry
{
    public struct Point2d
    {
        public double X,Y;
        public Point2d(double x,double y){X=x;Y=y;}
    }
    public struct Point3d
    {
        public double X,Y,Z;
        public Point3d(double x,double y,double z){X=x;Y=y;Z=z;}
    }
    public struct Vector3d {public static Vector3d ZAxis=>new Vector3d();}
}
namespace Autodesk.AutoCAD.DatabaseServices
{
    using Autodesk.AutoCAD.Geometry;
    public abstract class Curve:IDisposable {public virtual void Dispose(){}}
    public sealed class Circle:Curve
    {
        public Point3d Center;public double Radius;
        public Circle(Point3d center,Vector3d normal,double radius){Center=center;Radius=radius;}
    }
    public sealed class Polyline:Curve
    {
        public bool Closed;
        public readonly List<Point2d> Vertices=new List<Point2d>();
        public Polyline(int capacity){}
        public void AddVertexAt(int i,Point2d point,double bulge,double startWidth,double endWidth){Vertices.Insert(i,point);}
    }
}
static class SectionHatchBoundaryTests
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("Hatch boundary: "+name);checks++;Console.WriteLine("PASS hatch boundary: "+name);}
    static List<ParametricProjection.Curve2> Rectangle(double x,double y,double width,double height)
    {
        var p=new[]{new Point2(x,y),new Point2(x+width,y),new Point2(x+width,y+height),new Point2(x,y+height)};
        return Enumerable.Range(0,4).Select(i=>new ParametricProjection.Curve2{X0=p[i].X,Y0=p[i].Y,X1=p[(i+1)%4].X,Y1=p[(i+1)%4].Y}).ToList();
    }
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,name);}
    internal static void Run()
    {
        var wall=Rectangle(40,-100,10,200);
        using(var curve=SectionHatchBoundary.Create(wall))
        {
            var p=curve as Autodesk.AutoCAD.DatabaseServices.Polyline;
            Check(p!=null&&p.Closed,"pipe wall uses an explicitly CLOSED native polyline");
            Check(p.Vertices.Count==4,"four rectangle vertices, no zero-length closing edge");
            double twice=Enumerable.Range(0,4).Sum(i=>p.Vertices[i].X*p.Vertices[(i+1)%4].Y-p.Vertices[(i+1)%4].X*p.Vertices[i].Y);
            Check(Math.Abs(Math.Abs(twice)/2-2000)<1e-9,"whole rectangular wall area, not triangular fragments");
        }
        var ring=new[]{50.0,40.0}.Select(r=>new List<ParametricProjection.Curve2>{new ParametricProjection.Curve2{Circular=true,X0=250,Y0=-300,Radius=r,Sweep=2*Math.PI}}).ToList();
        foreach(var loop in ring)
            using(var curve=SectionHatchBoundary.Create(loop))
            {
                var circle=curve as Autodesk.AutoCAD.DatabaseServices.Circle;
                Check(circle!=null&&circle.Radius==loop[0].Radius&&circle.Center.X==250&&circle.Center.Y==-300,"ring boundary is a complete native circle with correct placement");
            }
        Reject(()=>SectionHatchBoundary.Create(new List<ParametricProjection.Curve2>()),"empty loop rejected");
        var open=Rectangle(0,0,10,10);open.RemoveAt(3);
        Reject(()=>SectionHatchBoundary.Create(open),"missing closing side rejected");
        var gap=Rectangle(0,0,10,10);gap[1].X0+=1;
        Reject(()=>SectionHatchBoundary.Create(gap),"disconnected boundary rejected");
        Reject(()=>SectionHatchBoundary.Create(new List<ParametricProjection.Curve2>{new ParametricProjection.Curve2{Circular=true,Radius=50,Sweep=Math.PI}}),"semicircle cannot masquerade as a closed ring");
        Console.WriteLine(checks+" hatch boundary checks passed (API double; native rendering still requires AutoCAD)");
    }
}
