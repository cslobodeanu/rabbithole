using System.Collections.Generic;

namespace SpatialForms
{
    public class SFElement
    {
        public int index;
        public List<SFElement> links;

        public XrProbe.Result data;

        /// <summary>0 = the timeline row; 1, 2... = related rows below it.</summary>
        public int row;

        /// <summary>For a related element: the timeline element whose column it
        /// sits in. Its x follows that element, which re-indexes as results
        /// arrive in date order.</summary>
        public SFElement column;

        /// <summary>Sideways shift in cells from its column (related grid: -1, 0, 1).</summary>
        public int columnOffset;

        /// <summary>Sideways scroll of a related row, in columns (fractional while it moves).</summary>
        public float shift;

        /// <summary>The column index this element is laid out in.</summary>
        public int columnIndex => column != null ? column.index : index;
    }
}
