using System;
namespace KotNode.Core
{
    public static class AnnotationPlacement
    {
        // Compare native anchor movement, not only text/line movement: a view may
        // have been moved alone or together with its external annotations.
        public static Point2 Capture(Point2 actual,Point2 nativeReference,Point2 lastReference,Point2 viewDelta)
        {
            bool viewMoved=Math.Abs(viewDelta.X)+Math.Abs(viewDelta.Y)>1e-6;
            bool anchorsStayed=Math.Abs(nativeReference.X-(lastReference.X-viewDelta.X))<1e-4
                &&Math.Abs(nativeReference.Y-(lastReference.Y-viewDelta.Y))<1e-4;
            return new Point2(actual.X-lastReference.X+(viewMoved&&anchorsStayed?viewDelta.X:0),
                actual.Y-lastReference.Y+(viewMoved&&anchorsStayed?viewDelta.Y:0));
        }
    }
}
