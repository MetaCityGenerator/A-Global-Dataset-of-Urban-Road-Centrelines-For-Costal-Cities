using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetaCity.Algorithms
{
    public static class Extension2D
    {
        public static Point GetInteriorPoint(this Geometry geo)
        {
            var centroid = geo.Centroid;
            if (geo.Contains(centroid))
            {
                return centroid;
            }
            else
            {
                var insidePt = InteriorPoint.GetInteriorCoord(geo);
                return new Point(insidePt);
            }
        }
    }
}
