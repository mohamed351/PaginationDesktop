using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms.VisualStyles;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    public enum FilterOperator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        EndsWith,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual
    }

    /// <summary>
    /// One runtime filter condition: "PropertyName Operator Value". Pass one or more of these
    /// to GridPaging.SetFilters(...) to filter the underlying query server-side.
    ///
    /// Multiple criteria are combined with AND, and are ANDed with whatever's in the search box
    /// too. Contains/StartsWith/EndsWith only apply to string properties. Value is converted to
    /// the property's actual type (including nullable and enum properties). If a criterion's
    /// PropertyName doesn't exist on the bound entity, or its Operator doesn't make sense for
    /// the property's type (e.g. Contains on an int), that one criterion is silently skipped
    /// rather than throwing - so callers can pass a broad, generic set of filter options without
    /// worrying about which ones apply to a given entity.
    /// </summary>
    public class FilterCriterion
    {
        public string PropertyName { get; set; }
        public FilterOperator Operator { get; set; } = FilterOperator.Equals;
        public object Value { get; set; }

        public FilterCriterion() { }

        public FilterCriterion(string propertyName, FilterOperator @operator, object value)
        {
            PropertyName = propertyName;
            Operator = @operator;
            Value = value;
        }
    }

    internal class CheckBoxHeaderCell : DataGridViewColumnHeaderCell
    {
        private const int CheckBoxSize = 13;

        public bool Checked { get; set; }

        public event EventHandler CheckedChanged;

        protected override void Paint(
            Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
            DataGridViewElementStates dataGridViewElementState, object value, object formattedValue, string errorText,
            DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, dataGridViewElementState, value, formattedValue,
                errorText, cellStyle, advancedBorderStyle, paintParts);

            var checkBoxLocation = new Point(
                cellBounds.Left + (cellBounds.Width - CheckBoxSize) / 2,
                cellBounds.Top + (cellBounds.Height - CheckBoxSize) / 2);

            CheckBoxRenderer.DrawCheckBox(graphics, checkBoxLocation,
                Checked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);
        }

        protected override void OnMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnMouseClick(e);
            Checked = !Checked;
            DataGridView?.InvalidateCell(this);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
