using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Entity;
using System.Drawing;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    /// <summary>
    /// Single, reusable WinForms UserControl: a DataGridView (Virtual Mode) + a search box +
    /// a BindingNavigator-style paging strip - for any EF6 DbContext / DbSet&lt;T&gt;, picked
    /// entirely through the Properties window at design time. Drop it on a form, then in the
    /// Properties grid set:
    ///
    ///   DbContextTypeName  -&gt; pick your DbContext from the dropdown
    ///   DbSetPropertyName  -&gt; pick the DbSet&lt;T&gt; property on that context (this is how the
    ///                         entity/"class for DbSet" is chosen)
    ///
    /// That's it - at runtime the control resolves the entity type from the chosen DbSet
    /// property, generates grid columns and a cross-column search filter via reflection, and
    /// starts paging automatically. No code required, though <see cref="Bind"/>,
    /// <see cref="AddColumn"/>, and <see cref="Refresh(int)"/> are all available if you want
    /// more control from code instead of (or in addition to) the designer.
    ///
    /// Because the entity type is only known at runtime (from the chosen property, not a C#
    /// generic parameter), this control is a plain, non-generic UserControl - which also means
    /// the WinForms designer can host it directly, no closed-subclass workaround needed.
    /// </summary>
    /// <summary>
    /// Single, reusable WinForms UserControl: a DataGridView (Virtual Mode) + a search box +
    /// a BindingNavigator-style paging strip - for any EF6 DbContext / DbSet&lt;T&gt;, picked
    /// entirely through the Properties window at design time.
    ///
    ///   DbContextTypeName  -&gt; pick your DbContext from the dropdown
    ///   DbSetPropertyName  -&gt; pick the DbSet&lt;T&gt; property on that context (this is how the
    ///                         entity/"class for DbSet" is chosen)
    ///   DefaultSortColumn  -&gt; optional; if left blank, the first data-bound column is used
    ///                         (SQL Server's OFFSET/FETCH paging requires an ORDER BY, so a sort
    ///                         column is always applied one way or another - see PageLoader's
    ///                         FallbackOrderProperty for the last-resort safety net)
    ///
    /// Columns default to plain text; use AddCheckBoxColumn/AddButtonColumn to add a column of a
    /// specific type from the start, or SetColumnType to change an already-added (or
    /// auto-generated) column's type afterwards. Call SetFilters(...) at any time to filter the
    /// underlying query server-side.
    /// </summary>
    /// <summary>
    /// Single, reusable WinForms UserControl: a DataGridView (Virtual Mode) + a search box +
    /// a BindingNavigator-style paging strip - for any EF6 DbContext / DbSet&lt;T&gt;, picked
    /// entirely through the Properties window at design time.
    ///
    ///   DbContextTypeName  -&gt; pick your DbContext from the dropdown
    ///   DbSetPropertyName  -&gt; pick the DbSet&lt;T&gt; property on that context (this is how the
    ///                         entity/"class for DbSet" is chosen)
    ///   DefaultSortColumn  -&gt; optional; if left blank, the first data-bound column is used
    ///                         (SQL Server's OFFSET/FETCH paging requires an ORDER BY, so a sort
    ///                         column is always applied one way or another - see PageLoader's
    ///                         FallbackOrderProperty for the last-resort safety net)
    ///
    /// Columns default to plain text; use AddCheckBoxColumn/AddButtonColumn to add a column of a
    /// specific type from the start, or SetColumnType to change an already-added (or
    /// auto-generated) column's type afterwards. Call SetFilters(...) at any time to filter the
    /// underlying query server-side.
    /// </summary>
    public partial class GridPaging : UserControl
    {
        private readonly Dictionary<string, Func<object, object>> _accessors = new Dictionary<string, Func<object, object>>();
        private readonly Dictionary<string, Action<object, object>> _setters = new Dictionary<string, Action<object, object>>();
        private readonly Dictionary<string, CheckBoxColumnInfo> _checkBoxColumns = new Dictionary<string, CheckBoxColumnInfo>();
        private readonly Dictionary<string, Action<object>> _buttonColumns = new Dictionary<string, Action<object>>();
        private readonly Dictionary<string, Dictionary<int, bool>> _selection = new Dictionary<string, Dictionary<int, bool>>();
        private readonly Dictionary<string, Func<object, string>> _textColumns = new Dictionary<string, Func<object, string>>();
        private readonly Dictionary<string, Dictionary<int, string>> _cellTextOverrides = new Dictionary<string, Dictionary<int, string>>();
        private readonly List<FilterCriterion> _filters = new List<FilterCriterion>();

        private List<object> _currentPageItems = new List<object>();
        private string _sortColumn;
        private bool _sortAscending = true;
        private bool _isLoading;
        private bool _boundOnce;

        private Type _contextType;
        private PropertyInfo _dbSetProperty;
        private IPageLoader _pageLoader;

        private class CheckBoxColumnInfo
        {
            public string PropertyName;              // null => unbound "selection" checkbox column
            public Type PropertyType;                 // underlying storage type (bool, int, enum, ...) - null if unbound
            public Action<object, object> Setter;     // null if unbound or read-only
            public CheckBoxHeaderCell HeaderCell;      // null if no select-all header
        }

        public GridPaging()
        {
            InitializeComponent();
            WireEvents();
            if (cboPageSize.Items.Contains(PageSize.ToString()))
                cboPageSize.SelectedItem = PageSize.ToString();
        }

        // ---- Design-time-selectable data source ---------------------------------------------

        /// <summary>
        /// Assembly-qualified name of the DbContext type to use. Shown as a dropdown in the
        /// Properties window (via <see cref="DbContextTypeConverter"/>) listing every
        /// DbContext-derived type found in the project/loaded assemblies.
        /// </summary>
        [Category("Data")]
        [Description("The DbContext to query. Pick one from the dropdown.")]
        [TypeConverter(typeof(DbContextTypeConverter))]
        [DefaultValue(null)]
        public string DbContextTypeName
        {
            get => _dbContextTypeName;
            set
            {
                if (_dbContextTypeName == value) return;
                _dbContextTypeName = value;
                DbSetPropertyName = null; // dependent selection, must be re-picked
            }
        }
        private string _dbContextTypeName;

        /// <summary>
        /// Name of the DbSet&lt;T&gt; property on <see cref="DbContextTypeName"/> to page through.
        /// This is how the entity type ("class for DbSet") is chosen - shown as a dropdown (via
        /// <see cref="DbSetPropertyConverter"/>) once DbContextTypeName is set.
        /// </summary>
        [Category("Data")]
        [Description("The DbSet<T> property to page through - this selects the entity type.")]
        [TypeConverter(typeof(DbSetPropertyConverter))]
        [DefaultValue(null)]
        public string DbSetPropertyName { get; set; }

        [Category("Data")]
        [DefaultValue(50)]
        public int PageSize { get; set; } = 50;

        /// <summary>
        /// Property name to sort by until the user clicks a column header. Leave blank to use
        /// the first data-bound column automatically. A sort column is always applied one way or
        /// another (see remarks on the class) because SQL Server's OFFSET/FETCH paging requires
        /// an ORDER BY clause.
        /// </summary>
        [Category("Behavior")]
        [Description("Property to sort by until a column header is clicked. Leave blank to use the first column automatically.")]
        [DefaultValue(null)]
        public string DefaultSortColumn { get; set; }

        [Category("Behavior")]
        [DefaultValue(true)]
        public bool DefaultSortAscending { get; set; } = true;

        [Browsable(false)]
        public Type EntityType { get; private set; }

        [Browsable(false)]
        public int CurrentPage { get; private set; } = 1;

        [Browsable(false)]
        public int TotalPages { get; private set; } = 1;

        [Browsable(false)]
        public int TotalRecords { get; private set; }

        /// <summary>The underlying grid, exposed for extra styling/eventing if needed.</summary>
        [Browsable(false)]
        public DataGridView Grid => dataGridView1;

        /// <summary>Rows currently shown on screen (read-only snapshot, not the live cache).</summary>
        [Browsable(false)]
        public IReadOnlyList<object> CurrentPageItems => _currentPageItems;

        /// <summary>Active runtime filters (see <see cref="SetFilters"/>).</summary>
        [Browsable(false)]
        public IReadOnlyList<FilterCriterion> ActiveFilters => _filters;

        /// <summary>Raised if a page load throws. The grid keeps showing the last good page.</summary>
        public event EventHandler<Exception> LoadError;

        // ---- Binding -------------------------------------------------------------------------

        /// <summary>
        /// Resolves DbContextTypeName/DbSetPropertyName via reflection and loads page 1. Called
        /// automatically on first Load at runtime if both properties are already set (e.g. set
        /// via the Properties window at design time) - call it yourself only if you set the
        /// properties in code after the control has already loaded.
        /// </summary>
        public void Bind()
        {
            if (string.IsNullOrEmpty(DbContextTypeName) || string.IsNullOrEmpty(DbSetPropertyName))
                throw new InvalidOperationException(
                    "Set DbContextTypeName and DbSetPropertyName first (in the Properties window or in code) before calling Bind().");

            _contextType = DbSetPropertyConverter.ResolveType(DbContextTypeName)
                ?? throw new InvalidOperationException($"Could not resolve DbContext type '{DbContextTypeName}'.");

            _dbSetProperty = _contextType.GetProperty(DbSetPropertyName)
                ?? throw new InvalidOperationException($"Property '{DbSetPropertyName}' not found on '{_contextType.Name}'.");

            if (!DbSetPropertyConverter.IsDbSetProperty(_dbSetProperty))
                throw new InvalidOperationException($"Property '{DbSetPropertyName}' is not a DbSet<T>.");

            EntityType = _dbSetProperty.PropertyType.GetGenericArguments()[0];
            _pageLoader = (IPageLoader)Activator.CreateInstance(typeof(PageLoader<>).MakeGenericType(EntityType));

            _accessors.Clear();
            _setters.Clear();
            if (dataGridView1.Columns.Count == 0)
                AutoGenerateColumns();

            // A sort column is required (see class remarks) - use the configured default, or
            // fall back to the first data-bound column. PageLoader has its own last-resort
            // fallback too, in case neither of those resolves to a real property.
            _sortColumn = !string.IsNullOrEmpty(DefaultSortColumn)
                ? DefaultSortColumn
                : dataGridView1.Columns.Cast<DataGridViewColumn>()
                    .Where(c => c.SortMode == DataGridViewColumnSortMode.Programmatic)
                    .Select(c => c.DataPropertyName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));
            _sortAscending = DefaultSortAscending;
            ApplySortGlyphs();

            _boundOnce = true;
            _ = LoadPageAsync(1);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (!DesignMode && !_boundOnce && !string.IsNullOrEmpty(DbContextTypeName) && !string.IsNullOrEmpty(DbSetPropertyName))
                Bind();
        }

        // ---- Columns -----------------------------------------------------------------------

        /// <summary>
        /// Manually adds/binds a plain text column. Call this before <see cref="Bind"/> for any
        /// property you want to customize (header text, width, format) - once at least one
        /// column exists, automatic column generation is skipped entirely.
        ///
        /// Pass <paramref name="propertyName"/> as null/empty for a column not bound to any
        /// property (e.g. <c>AddColumn(null, "Status")</c>) - equivalent to calling
        /// <see cref="AddEmptyColumn"/>; use <see cref="SetCellText(string,int,string)"/> or the
        /// row/column-index overload to fill in its cells.
        /// </summary>
        public DataGridViewColumn AddColumn(string propertyName, string headerText = null, int width = 120, string format = null)
        {
            if (string.IsNullOrEmpty(propertyName))
                return AddEmptyColumn(headerText, width);

            var column = BuildTextColumn(propertyName, propertyName, headerText, width, format);
            dataGridView1.Columns.Add(column);
            return column;
        }

        /// <summary>
        /// Adds a checkbox column. Pass <paramref name="propertyName"/> to bind it to a bool
        /// property on the entity (edits apply to the in-memory row object only, not written
        /// back to the database); leave it null for a pure "selection" column not tied to any
        /// property - read which rows are checked via <see cref="GetSelectedItems"/>.
        ///
        /// With <paramref name="showSelectAllHeader"/> (default true), clicking the header
        /// checkbox toggles every row currently on screen - see <see cref="CheckBoxHeaderCell"/>
        /// remarks for why this only applies to the current page.
        /// </summary>
        public DataGridViewColumn AddCheckBoxColumn(string propertyName = null, string headerText = null, int width = 60, bool showSelectAllHeader = true)
        {
            var columnName = propertyName ?? $"__select_{Guid.NewGuid():N}";
            var column = BuildCheckBoxColumn(columnName, propertyName, headerText, width, showSelectAllHeader);
            dataGridView1.Columns.Add(column);
            return column;
        }

        /// <summary>
        /// Adds a button column. <paramref name="onClick"/> is invoked with the row's entity
        /// object whenever a button in this column is clicked.
        /// </summary>
        public DataGridViewColumn AddButtonColumn(string buttonText, Action<object> onClick, string headerText = "", int width = 90)
        {
            if (onClick == null) throw new ArgumentNullException(nameof(onClick));
            var columnName = $"__button_{Guid.NewGuid():N}";
            var column = BuildButtonColumn(columnName, buttonText, headerText, width);
            dataGridView1.Columns.Add(column);
            _buttonColumns[columnName] = onClick;
            return column;
        }

        /// <summary>
        /// Adds a plain text column not bound to any entity property - e.g. for a computed
        /// value, a spacer, or static/ad-hoc content. Returns the created column so you can grab
        /// its <c>Name</c> to use with <see cref="SetCellText"/>.
        ///
        /// Pass <paramref name="textProvider"/> to compute each cell's text from the row's
        /// entity object every time it's rendered (e.g. <c>c => $"{((Customer)c).Name} ({((Customer)c).Country})"</c>).
        /// Leave it null to control the text yourself, cell by cell, via <see cref="SetCellText"/> -
        /// cells default to blank until you set them.
        /// </summary>
        public DataGridViewColumn AddEmptyColumn(string headerText = "", int width = 100, Func<object, string> textProvider = null)
        {
            var columnName = $"__empty_{Guid.NewGuid():N}";
            var column = new DataGridViewTextBoxColumn
            {
                Name = columnName,
                DataPropertyName = null, // unbound - not tied to any entity property
                HeaderText = headerText ?? string.Empty,
                Width = width > 0 ? width : 100,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };

            dataGridView1.Columns.Add(column);
            _textColumns[columnName] = textProvider; // may be null - see SetCellText
            return column;
        }

        /// <summary>
        /// Same as <see cref="SetCellText(string,int,string)"/>, but identifies the column by
        /// its position instead of its name - handy when you already have both indices (e.g.
        /// from a loop over the grid) rather than the column's Name.
        /// </summary>
        public void SetCellText(int rowIndex, int columnIndex, string value)
        {
            if (columnIndex < 0 || columnIndex >= dataGridView1.Columns.Count) return;
            SetCellText(dataGridView1.Columns[columnIndex].Name, rowIndex, value);
        }

        /// <summary>
        /// Sets the display text for one cell in a column added via <see cref="AddEmptyColumn"/>
        /// (with no <c>textProvider</c>). <paramref name="rowIndex"/> is relative to the current
        /// page (0-based) - like the rest of this control's row-level state, it only applies to
        /// rows currently on screen and is cleared on the next page/search/sort/filter reload.
        /// Has no effect on a column that has a <c>textProvider</c> - that always wins.
        /// </summary>
        public void SetCellText(string columnName, int rowIndex, string text)
        {
            if (!_cellTextOverrides.TryGetValue(columnName, out var map))
                _cellTextOverrides[columnName] = map = new Dictionary<int, string>();
            map[rowIndex] = text;

            var columnIndex = FindColumnIndex(columnName);
            if (columnIndex >= 0 && rowIndex >= 0 && rowIndex < dataGridView1.RowCount)
                dataGridView1.InvalidateCell(columnIndex, rowIndex);
        }

        /// <summary>
        /// Changes the type of an already-added column (whether it was auto-generated or added
        /// via AddColumn/AddCheckBoxColumn/AddButtonColumn) - e.g. to turn an auto-generated text
        /// column for a bool property into a proper checkbox column, or vice versa. If no column
        /// exists yet for <paramref name="propertyName"/>, one is added fresh at the end.
        ///
        /// Width/headerText/format are optional - anything left null/zero is carried over from
        /// the existing column (or defaulted sensibly if there's no existing column yet).
        /// <paramref name="buttonText"/>/<paramref name="onButtonClick"/> are required when
        /// <paramref name="columnType"/> is <see cref="GridColumnType.Button"/>.
        /// </summary>
        public DataGridViewColumn SetColumnType(
            string propertyName,
            GridColumnType columnType,
            string headerText = null,
            int width = 0,
            string format = null,
            bool showSelectAllHeader = true,
            string buttonText = null,
            Action<object> onButtonClick = null)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentException("propertyName is required.", nameof(propertyName));

            var existingIndex = FindColumnIndex(propertyName);
            var resolvedHeaderText = headerText
                ?? (existingIndex >= 0 ? dataGridView1.Columns[existingIndex].HeaderText : null);
            var resolvedWidth = width > 0 ? width
                : existingIndex >= 0 ? dataGridView1.Columns[existingIndex].Width
                : DefaultWidthFor(columnType);

            if (existingIndex >= 0)
                RemoveColumn(propertyName);

            DataGridViewColumn newColumn;
            switch (columnType)
            {
                case GridColumnType.CheckBox:
                    newColumn = BuildCheckBoxColumn(propertyName, propertyName, resolvedHeaderText, resolvedWidth, showSelectAllHeader);
                    break;

                case GridColumnType.Button:
                    if (onButtonClick == null)
                        throw new ArgumentException("onButtonClick is required when columnType is Button.", nameof(onButtonClick));
                    newColumn = BuildButtonColumn(propertyName, buttonText ?? resolvedHeaderText ?? propertyName, resolvedHeaderText, resolvedWidth);
                    break;

                default:
                    newColumn = BuildTextColumn(propertyName, propertyName, resolvedHeaderText, resolvedWidth, format);
                    break;
            }

            if (existingIndex >= 0 && existingIndex <= dataGridView1.Columns.Count)
                dataGridView1.Columns.Insert(existingIndex, newColumn);
            else
                dataGridView1.Columns.Add(newColumn);

            if (columnType == GridColumnType.Button)
                _buttonColumns[newColumn.Name] = onButtonClick;

            return newColumn;
        }

        /// <summary>Removes a column (by the name it was given via AddColumn/AddCheckBoxColumn/AddButtonColumn/SetColumnType) and its bookkeeping. Returns false if no such column exists.</summary>
        public bool RemoveColumn(string columnName)
        {
            var index = FindColumnIndex(columnName);
            if (index < 0) return false;

            // _accessors/_setters are keyed by the entity's real property name (DataPropertyName),
            // which can differ from the column's Name after RenameColumn - look it up properly
            // rather than assuming they match.
            var dataPropertyName = dataGridView1.Columns[index].DataPropertyName;
            if (!string.IsNullOrEmpty(dataPropertyName))
            {
                _accessors.Remove(dataPropertyName);
                _setters.Remove(dataPropertyName);
            }

            _checkBoxColumns.Remove(columnName);
            _buttonColumns.Remove(columnName);
            _selection.Remove(columnName);
            _textColumns.Remove(columnName);
            _cellTextOverrides.Remove(columnName);
            dataGridView1.Columns.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Renames an existing column's internal identifier (<c>DataGridViewColumn.Name</c> -
        /// what <see cref="RemoveColumn"/>, <see cref="SetColumnType"/>, and
        /// <see cref="SetCellText(string,int,string)"/> look columns up by). This is distinct
        /// from the visible header text (pass <c>headerText</c> to AddColumn/SetColumnType for
        /// that) and from the entity property it's bound to (which never changes - a column
        /// keeps reading/writing the same property regardless of what you call it). Mainly
        /// useful for giving an auto-generated synthetic column name (e.g. the GUID-based name
        /// AddButtonColumn/an unbound AddCheckBoxColumn assigns) a friendlier identifier to
        /// reference later. Returns false if no column named <paramref name="oldName"/> exists;
        /// throws if <paramref name="newName"/> is already taken by another column.
        /// </summary>
        public bool RenameColumn(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(newName))
                throw new ArgumentException("newName is required.", nameof(newName));
            if (oldName == newName)
                return FindColumnIndex(oldName) >= 0;

            var index = FindColumnIndex(oldName);
            if (index < 0) return false;

            if (FindColumnIndex(newName) >= 0)
                throw new ArgumentException($"A column named '{newName}' already exists.", nameof(newName));

            dataGridView1.Columns[index].Name = newName;

            MoveKey(_checkBoxColumns, oldName, newName);
            MoveKey(_buttonColumns, oldName, newName);
            MoveKey(_textColumns, oldName, newName);
            MoveKey(_selection, oldName, newName);
            MoveKey(_cellTextOverrides, oldName, newName);

            return true;
        }

        private static void MoveKey<TValue>(Dictionary<string, TValue> dict, string oldKey, string newKey)
        {
            if (dict.TryGetValue(oldKey, out var value))
            {
                dict.Remove(oldKey);
                dict[newKey] = value;
            }
        }

        /// <summary>
        /// Moves a column to a new visual position, identified by its header text (falls back to
        /// matching by column/property name if no header text matches). Uses
        /// <see cref="DataGridViewColumn.DisplayIndex"/> rather than physically moving the column
        /// within the Columns collection, so nothing else (accessors, checkbox/button/text-column
        /// bookkeeping - all keyed by column name) is affected. <paramref name="index"/> is
        /// clamped to a valid range. Returns false if no column matches
        /// <paramref name="headerName"/>.
        /// </summary>
        public bool SetColumnIndex(string headerName, int index)
        {
            var column = FindColumnByHeaderOrName(headerName);
            if (column == null) return false;

            column.DisplayIndex = Math.Max(0, Math.Min(index, dataGridView1.Columns.Count - 1));
            return true;
        }

        /// <summary>Shows or hides a column, identified by its header text (or, failing that, its column/property name).</summary>
        public bool SetColumnVisible(string headerOrColumnName, bool visible)
        {
            var column = FindColumnByHeaderOrName(headerOrColumnName);
            if (column == null) return false;

            column.Visible = visible;
            return true;
        }

        /// <summary>Shortcut for <c>SetColumnVisible(headerOrColumnName, false)</c>.</summary>
        public bool HideColumn(string headerOrColumnName) => SetColumnVisible(headerOrColumnName, false);

        /// <summary>Shortcut for <c>SetColumnVisible(headerOrColumnName, true)</c>.</summary>
        public bool ShowColumn(string headerOrColumnName) => SetColumnVisible(headerOrColumnName, true);

        private DataGridViewColumn FindColumnByHeaderOrName(string headerOrColumnName)
        {
            var columns = dataGridView1.Columns.Cast<DataGridViewColumn>();
            return columns.FirstOrDefault(c => string.Equals(c.HeaderText, headerOrColumnName, StringComparison.OrdinalIgnoreCase))
                ?? columns.FirstOrDefault(c => string.Equals(c.Name, headerOrColumnName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Rows on the current page whose unbound "selection" checkbox is checked (see AddCheckBoxColumn). If more than one such column exists, pass its column name to disambiguate.</summary>
        /// <summary>
        /// Rows on the current page whose checkbox is currently checked - works for both an
        /// unbound "selection" checkbox column (see AddCheckBoxColumn) and a checkbox bound to
        /// a bool/numeric/enum property (its live in-memory value is used). If more than one
        /// checkbox column exists, pass its column name to disambiguate; otherwise the first one
        /// found is used.
        /// </summary>
        public IReadOnlyList<object> GetSelectedItems(string columnName = null)
        {
            var key = columnName ?? _checkBoxColumns.Keys.FirstOrDefault();
            if (key == null || !_checkBoxColumns.TryGetValue(key, out var info))
                return new List<object>();

            var result = new List<object>();
            for (var i = 0; i < _currentPageItems.Count; i++)
            {
                bool isChecked;
                if (info.PropertyName != null && _accessors.TryGetValue(info.PropertyName, out var accessor))
                    isChecked = ToCheckBoxBool(accessor(_currentPageItems[i]));
                else
                    isChecked = _selection.TryGetValue(key, out var map) && map.TryGetValue(i, out var selected) && selected;

                if (isChecked)
                    result.Add(_currentPageItems[i]);
            }
            return result;
        }

        /// <summary>
        /// Convenience for the common "give me the IDs of the checked rows" case: reads
        /// <paramref name="idPropertyName"/> off every row returned by <see cref="GetSelectedItems"/>.
        ///
        /// Use this together with an UNBOUND selection checkbox column
        /// (<c>AddCheckBoxColumn(propertyName: null, ...)</c>) rather than binding the checkbox
        /// directly to your ID property - binding a checkbox straight to an ID column means
        /// checking/unchecking it overwrites that row's actual ID (to 1/0) in memory, which is
        /// almost never what you want. Keep the ID as a normal text column, add a separate
        /// unbound checkbox column for selection, and use this method to read the IDs back.
        /// </summary>
        public IReadOnlyList<object> GetSelectedIds(string idPropertyName, string columnName = null)
        {
            if (!_accessors.TryGetValue(idPropertyName, out var idAccessor))
            {
                var entityType = EntityType ?? ResolveEntityTypeForDesignTimeColumnSetup();
                var property = entityType.GetProperty(idPropertyName)
                    ?? throw new ArgumentException($"Property '{idPropertyName}' not found on type {entityType.Name}.", nameof(idPropertyName));
                idAccessor = CompileAccessor(entityType, property);
                _accessors[idPropertyName] = idAccessor;
            }

            return GetSelectedItems(columnName).Select(idAccessor).ToList();
        }

        private int FindColumnIndex(string columnName) =>
            dataGridView1.Columns.Cast<DataGridViewColumn>()
                .Select((c, i) => new { Column = c, Index = i })
                .Where(x => x.Column.Name == columnName)
                .Select(x => x.Index)
                .DefaultIfEmpty(-1)
                .First();

        private static int DefaultWidthFor(GridColumnType columnType) =>
            columnType == GridColumnType.CheckBox ? 60 : columnType == GridColumnType.Button ? 90 : 120;

        private DataGridViewTextBoxColumn BuildTextColumn(string columnName, string propertyName, string headerText, int width, string format)
        {
            var entityType = EntityType ?? ResolveEntityTypeForDesignTimeColumnSetup();
            var property = entityType.GetProperty(propertyName)
                ?? throw new ArgumentException($"Property '{propertyName}' not found on type {entityType.Name}.", nameof(propertyName));

            if (!_accessors.ContainsKey(propertyName))
                _accessors[propertyName] = CompileAccessor(entityType, property);

            var column = new DataGridViewTextBoxColumn
            {
                Name = columnName,
                DataPropertyName = propertyName,
                HeaderText = headerText ?? Humanize(propertyName),
                Width = width > 0 ? width : 120,
                ReadOnly = true, // grid-level ReadOnly is false (checkbox/button columns need it); keep text columns display-only
                SortMode = DataGridViewColumnSortMode.Programmatic
            };
            if (!string.IsNullOrEmpty(format))
                column.DefaultCellStyle.Format = format;

            return column;
        }

        private DataGridViewCheckBoxColumn BuildCheckBoxColumn(string columnName, string propertyName, string headerText, int width, bool showSelectAllHeader)
        {
            Action<object, object> setter = null;
            Type propertyType = null;

            if (!string.IsNullOrEmpty(propertyName))
            {
                var entityType = EntityType ?? ResolveEntityTypeForDesignTimeColumnSetup();
                var property = entityType.GetProperty(propertyName)
                    ?? throw new ArgumentException($"Property '{propertyName}' not found on type {entityType.Name}.", nameof(propertyName));

                if (!IsCheckBoxCompatibleType(property.PropertyType))
                    throw new ArgumentException(
                        $"Property '{propertyName}' (type {property.PropertyType.Name}) can't back a checkbox column - " +
                        "bool, numeric (int, byte, long, decimal, ...), and enum properties are supported.",
                        nameof(propertyName));

                propertyType = property.PropertyType;

                if (!_accessors.ContainsKey(propertyName))
                    _accessors[propertyName] = CompileAccessor(entityType, property);

                if (property.CanWrite)
                {
                    setter = CompileSetter(entityType, property);
                    _setters[propertyName] = setter;
                }
            }

            var resolvedHeaderText = headerText ?? (propertyName != null ? Humanize(propertyName) : string.Empty);

            var column = new DataGridViewCheckBoxColumn
            {
                Name = columnName,
                DataPropertyName = propertyName,
                HeaderText = resolvedHeaderText,
                Width = width > 0 ? width : 60,
                // Always ReadOnly: toggling is handled entirely by our own CellClick/keyboard
                // logic (see DataGridView_CellClick, DataGridView_KeyDown), not WinForms' own
                // click-to-edit-checkbox pipeline - which in Virtual Mode is unreliable about
                // registering the very first click on a cell that wasn't already current.
                // ReadOnly only blocks the *editing* pipeline, not click/key events, so our own
                // handlers still fire normally; a read-only checkbox with a setter can still be
                // toggled by the user, just not through DataGridView's built-in mechanism.
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                // Required in Virtual Mode: without an explicit ValueType/TrueValue/FalseValue,
                // the checkbox cell can fail to interpret the value handed back from
                // CellValueNeeded and shows unchecked regardless of the real value, until the
                // cell has been manually toggled once (which "primes" it). Setting these
                // explicitly makes the checkbox reflect the actual data immediately.
                ValueType = typeof(bool),
                TrueValue = true,
                FalseValue = false
            };

            CheckBoxHeaderCell headerCell = null;
            if (showSelectAllHeader)
            {
                headerCell = new CheckBoxHeaderCell { Value = resolvedHeaderText };
                column.HeaderCell = headerCell;
                headerCell.CheckedChanged += (s, e) => ToggleAllOnCurrentPage(column, headerCell.Checked);
            }

            _checkBoxColumns[columnName] = new CheckBoxColumnInfo
            {
                PropertyName = propertyName,
                PropertyType = propertyType,
                Setter = setter,
                HeaderCell = headerCell
            };

            return column;
        }

        private static DataGridViewButtonColumn BuildButtonColumn(string columnName, string buttonText, string headerText, int width)
        {
            return new DataGridViewButtonColumn
            {
                Name = columnName,
                HeaderText = headerText ?? string.Empty,
                Text = buttonText,
                UseColumnTextForButtonValue = true,
                Width = width > 0 ? width : 90,
                ReadOnly = false,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }

        private void ToggleAllOnCurrentPage(DataGridViewColumn column, bool value)
        {
            for (var i = 0; i < _currentPageItems.Count; i++)
                SetCheckBoxValue(column.Name, i, value);
            dataGridView1.InvalidateColumn(column.Index);
        }

        /// <summary>
        /// Checkbox columns can bind to bool, or to a numeric/enum property used as a flag (the
        /// classic "1/0 as a boolean" pattern, e.g. an int Id-like column). This maps any of
        /// those raw storage values to true/false for display.
        /// </summary>
        private static bool ToCheckBoxBool(object rawValue)
        {
            switch (rawValue)
            {
                case null: return false;
                case bool b: return b;
                default:
                    try { return Convert.ToDouble(rawValue) != 0; }
                    catch { return false; }
            }
        }

        /// <summary>Converts a checkbox's true/false back into whatever type the bound property actually stores.</summary>
        private static object ConvertBoolToStorage(bool value, Type propertyType)
        {
            var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (underlying == typeof(bool)) return value;
            if (underlying.IsEnum) return Enum.ToObject(underlying, value ? 1 : 0);
            return Convert.ChangeType(value ? 1 : 0, underlying);
        }

        /// <summary>bool, or any numeric/enum type - all usable as a checkbox's underlying storage.</summary>
        private static bool IsCheckBoxCompatibleType(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying == typeof(bool)
                   || underlying.IsEnum
                   || underlying == typeof(byte) || underlying == typeof(sbyte)
                   || underlying == typeof(short) || underlying == typeof(ushort)
                   || underlying == typeof(int) || underlying == typeof(uint)
                   || underlying == typeof(long) || underlying == typeof(ulong)
                   || underlying == typeof(decimal) || underlying == typeof(float) || underlying == typeof(double);
        }

        private Type ResolveEntityTypeForDesignTimeColumnSetup()
        {
            if (string.IsNullOrEmpty(DbContextTypeName) || string.IsNullOrEmpty(DbSetPropertyName))
                throw new InvalidOperationException("Set DbContextTypeName and DbSetPropertyName (or call Bind()) before adding columns.");

            var contextType = DbSetPropertyConverter.ResolveType(DbContextTypeName)
                ?? throw new InvalidOperationException($"Could not resolve DbContext type '{DbContextTypeName}'.");
            var property = contextType.GetProperty(DbSetPropertyName)
                ?? throw new InvalidOperationException($"Property '{DbSetPropertyName}' not found on '{contextType.Name}'.");
            return property.PropertyType.GetGenericArguments()[0];
        }

        /// <summary>Compiles a Func&lt;object,object&gt; accessor for a property, without needing a compile-time T.</summary>
        private static Func<object, object> CompileAccessor(Type entityType, PropertyInfo property)
        {
            var objParam = Expression.Parameter(typeof(object), "x");
            var typedParam = Expression.Convert(objParam, entityType);
            var propertyAccess = Expression.Property(typedParam, property);
            var resultAsObject = Expression.Convert(propertyAccess, typeof(object));
            return Expression.Lambda<Func<object, object>>(resultAsObject, objParam).Compile();
        }

        /// <summary>Compiles an Action&lt;object,object&gt; setter for a property, without needing a compile-time T.</summary>
        private static Action<object, object> CompileSetter(Type entityType, PropertyInfo property)
        {
            var targetParam = Expression.Parameter(typeof(object), "target");
            var valueParam = Expression.Parameter(typeof(object), "value");
            var typedTarget = Expression.Convert(targetParam, entityType);
            var typedValue = Expression.Convert(valueParam, property.PropertyType);
            var assign = Expression.Assign(Expression.Property(typedTarget, property), typedValue);
            return Expression.Lambda<Action<object, object>>(assign, targetParam, valueParam).Compile();
        }

        /// <summary>Reloads the given page (e.g. after external data changes).</summary>
        public void Refresh(int page) => _ = LoadPageAsync(page);

        private void AutoGenerateColumns()
        {
            var properties = EntityType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead
                            && p.GetIndexParameters().Length == 0
                            && !Attribute.IsDefined(p, typeof(NotMappedAttribute))
                            && IsSimpleType(p.PropertyType));

            foreach (var property in properties)
            {
                if (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
                {
                    AddCheckBoxColumn(property.Name, Humanize(property.Name), 60, showSelectAllHeader: false);
                    continue;
                }

                var format = property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?)
                    ? "yyyy-MM-dd"
                    : null;
                AddColumn(property.Name, Humanize(property.Name), 130, format);
            }
        }

        private static bool IsSimpleType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive
                   || type.IsEnum
                   || type == typeof(string)
                   || type == typeof(decimal)
                   || type == typeof(DateTime)
                   || type == typeof(DateTimeOffset)
                   || type == typeof(Guid)
                   || type == typeof(TimeSpan);
        }

        private static string Humanize(string propertyName) =>
            Regex.Replace(propertyName, "(?<!^)([A-Z])", " $1");

        // ---- Runtime filtering ---------------------------------------------------------------

        /// <summary>Replaces the current runtime filter set and reloads page 1.</summary>
        public void SetFilters(params FilterCriterion[] filters) => SetFilters((IEnumerable<FilterCriterion>)filters);

        /// <summary>Replaces the current runtime filter set and reloads page 1.</summary>
        public void SetFilters(IEnumerable<FilterCriterion> filters)
        {
            _filters.Clear();
            if (filters != null) _filters.AddRange(filters);
            _ = LoadPageAsync(1);
        }

        /// <summary>Clears all runtime filters (search box is unaffected) and reloads page 1.</summary>
        public void ClearFilters()
        {
            if (_filters.Count == 0) return;
            _filters.Clear();
            _ = LoadPageAsync(1);
        }

        // ---- Wiring ------------------------------------------------------------------------

        private void WireEvents()
        {
            dataGridView1.CellValueNeeded += DataGridView_CellValueNeeded;
            dataGridView1.CellValuePushed += DataGridView_CellValuePushed;
            dataGridView1.CellContentClick += DataGridView_CellContentClick;
            dataGridView1.CellClick += DataGridView_CellClick;
            dataGridView1.KeyDown += DataGridView_KeyDown;
            dataGridView1.ColumnHeaderMouseClick += DataGridView_ColumnHeaderMouseClick;

            // Checkbox cells are now ReadOnly (toggling is driven entirely by our own
            // CellClick/KeyDown handlers - see ToggleCheckBoxCellIfApplicable), so this
            // rarely fires for them in practice. Left in place as a general virtual-mode
            // safety net in case a future editable (non-ReadOnly) column type is added.
            dataGridView1.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (dataGridView1.IsCurrentCellDirty)
                    dataGridView1.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            bindingNavigatorMoveFirstItem.Click += (s, e) => _ = LoadPageAsync(1);
            bindingNavigatorMovePreviousItem.Click += (s, e) => _ = LoadPageAsync(CurrentPage - 1);
            bindingNavigatorMoveNextItem.Click += (s, e) => _ = LoadPageAsync(CurrentPage + 1);
            bindingNavigatorMoveLastItem.Click += (s, e) => _ = LoadPageAsync(TotalPages);

            bindingNavigatorPositionItem.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (int.TryParse(bindingNavigatorPositionItem.Text, out var page))
                    _ = LoadPageAsync(page);
            };

            cboPageSize.SelectedIndexChanged += (s, e) =>
            {
                if (int.TryParse(cboPageSize.Text, out var size) && size > 0 && size != PageSize)
                {
                    PageSize = size;
                    _ = LoadPageAsync(1);
                }
            };

            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                _ = LoadPageAsync(1);
            };
        }

        private void DataGridView_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            var column = dataGridView1.Columns[e.ColumnIndex];

            // Checkbox/button columns are SortMode.NotSortable - clicking anywhere in their
            // header (including the select-all checkbox area) still fires this event, but must
            // never be treated as a sort request. Only plain text columns (SortMode.Programmatic,
            // set in BuildTextColumn) are sortable.
            if (column.SortMode != DataGridViewColumnSortMode.Programmatic) return;

            var propertyName = column.DataPropertyName;
            if (string.IsNullOrEmpty(propertyName)) return;

            if (_sortColumn == propertyName)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = propertyName;
                _sortAscending = true;
            }

            ApplySortGlyphs();
            _ = LoadPageAsync(1);
        }

        private void ApplySortGlyphs()
        {
            foreach (DataGridViewColumn column in dataGridView1.Columns)
            {
                var direction = column.DataPropertyName == _sortColumn
                    ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                    : SortOrder.None;

                // Setting a non-None SortGlyphDirection on a NotSortable column (checkbox/button
                // columns) throws InvalidOperationException - skip rather than crash. Data can
                // still be validly ORDER BY'd by this column even though the UI can't show a
                // sort glyph for it (see PageLoader's FallbackOrderProperty).
                if (column.SortMode == DataGridViewColumnSortMode.NotSortable && direction != SortOrder.None)
                    continue;

                column.HeaderCell.SortGlyphDirection = direction;
            }
        }

        private void DataGridView_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _currentPageItems.Count) return;

            var columnName = dataGridView1.Columns[e.ColumnIndex].Name;

            if (_checkBoxColumns.TryGetValue(columnName, out var checkBoxInfo))
            {
                e.Value = GetCheckBoxValue(columnName, e.RowIndex);
                return;
            }

            if (_textColumns.TryGetValue(columnName, out var textProvider))
            {
                if (textProvider != null)
                {
                    e.Value = textProvider(_currentPageItems[e.RowIndex]);
                }
                else
                {
                    e.Value = _cellTextOverrides.TryGetValue(columnName, out var textMap)
                              && textMap.TryGetValue(e.RowIndex, out var text)
                        ? text
                        : string.Empty;
                }
                return;
            }

            var propertyName = dataGridView1.Columns[e.ColumnIndex].DataPropertyName;
            if (!string.IsNullOrEmpty(propertyName) && _accessors.TryGetValue(propertyName, out var accessor))
                e.Value = accessor(_currentPageItems[e.RowIndex]);
        }

        private void DataGridView_CellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _currentPageItems.Count) return;

            var columnName = dataGridView1.Columns[e.ColumnIndex].Name;
            if (!_checkBoxColumns.ContainsKey(columnName)) return;

            SetCheckBoxValue(columnName, e.RowIndex, e.Value is bool b && b);
        }

        /// <summary>
        /// Single source of truth for reading a checkbox cell's current value - used by
        /// CellValueNeeded and the direct-click toggle handler alike.
        /// </summary>
        private bool GetCheckBoxValue(string columnName, int rowIndex)
        {
            if (!_checkBoxColumns.TryGetValue(columnName, out var info)) return false;

            if (info.PropertyName != null && _accessors.TryGetValue(info.PropertyName, out var accessor))
                return ToCheckBoxBool(accessor(_currentPageItems[rowIndex]));

            return _selection.TryGetValue(columnName, out var map) && map.TryGetValue(rowIndex, out var selected) && selected;
        }

        /// <summary>
        /// Single source of truth for writing a checkbox cell's value - used by
        /// CellValuePushed (keyboard/space-bar edits), the direct-click toggle handler, and
        /// the select-all header toggle alike.
        /// </summary>
        private void SetCheckBoxValue(string columnName, int rowIndex, bool value)
        {
            if (!_checkBoxColumns.TryGetValue(columnName, out var info)) return;

            if (info.Setter != null)
            {
                info.Setter(_currentPageItems[rowIndex], ConvertBoolToStorage(value, info.PropertyType));
            }
            else
            {
                if (!_selection.TryGetValue(columnName, out var map))
                    _selection[columnName] = map = new Dictionary<int, bool>();
                map[rowIndex] = value;
            }
        }

        private void DataGridView_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _currentPageItems.Count) return;

            var columnName = dataGridView1.Columns[e.ColumnIndex].Name;
            if (_buttonColumns.TryGetValue(columnName, out var handler))
                handler(_currentPageItems[e.RowIndex]);
        }

        private void DataGridView_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= _currentPageItems.Count) return;
            ToggleCheckBoxCellIfApplicable(dataGridView1.Columns[e.ColumnIndex].Name, e.RowIndex);
        }

        private void DataGridView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space) return;

            var cell = dataGridView1.CurrentCell;
            if (cell == null || cell.RowIndex < 0 || cell.ColumnIndex < 0 || cell.RowIndex >= _currentPageItems.Count) return;

            if (ToggleCheckBoxCellIfApplicable(dataGridView1.Columns[cell.ColumnIndex].Name, cell.RowIndex))
                e.Handled = true;
        }

        /// <summary>
        /// Toggles a checkbox cell if <paramref name="columnName"/> is a checkbox column that's
        /// actually editable (unbound selection column, or bound to a property with a public
        /// setter - a checkbox bound to a read-only property is displayed but not toggleable).
        /// Returns whether a toggle happened.
        /// </summary>
        private bool ToggleCheckBoxCellIfApplicable(string columnName, int rowIndex)
        {
            if (!_checkBoxColumns.TryGetValue(columnName, out var info)) return false;
            if (info.PropertyName != null && info.Setter == null) return false; // bound to a read-only property

            SetCheckBoxValue(columnName, rowIndex, !GetCheckBoxValue(columnName, rowIndex));

            var columnIndex = FindColumnIndex(columnName);
            if (columnIndex >= 0)
                dataGridView1.InvalidateCell(columnIndex, rowIndex);
            return true;
        }

        // ---- Loading -----------------------------------------------------------------------

        private async Task LoadPageAsync(int page)
        {
            if (_pageLoader == null || _isLoading) return;
            if (page < 1) page = 1;

            _isLoading = true;
            SetNavEnabled(false);
            try
            {
                // Fresh DbContext per page load keeps this simple and avoids change-tracker
                // bloat for what is fundamentally a read-only grid.
                using (var context = (DbContext)Activator.CreateInstance(_contextType))
                {
                    var dbSetValue = _dbSetProperty.GetValue(context);
                    var skip = (page - 1) * PageSize;

                    var result = await _pageLoader.LoadPageAsync(
                        dbSetValue, skip, PageSize, txtSearch.Text, _sortColumn, _sortAscending, _filters).ConfigureAwait(true);

                    var totalPages = Math.Max((int)Math.Ceiling(result.TotalCount / (double)PageSize), 1);
                    var currentPage = Math.Min(page, totalPages);

                    // If the requested page was out of range once we knew the true total, refetch
                    // the corrected page rather than showing an empty/misaligned page.
                    if (currentPage != page)
                    {
                        skip = (currentPage - 1) * PageSize;
                        result = await _pageLoader.LoadPageAsync(
                            dbSetValue, skip, PageSize, txtSearch.Text, _sortColumn, _sortAscending, _filters).ConfigureAwait(true);
                    }

                    _currentPageItems = result.Items.Cast<object>().ToList();
                    TotalRecords = result.TotalCount;
                    TotalPages = totalPages;
                    CurrentPage = currentPage;
                }

                _selection.Clear(); // row indices only make sense for the page they were set on
                _cellTextOverrides.Clear(); // ditto for ad-hoc SetCellText overrides

                dataGridView1.RowCount = 0; // forces a full virtual-mode repaint
                dataGridView1.RowCount = _currentPageItems.Count;
                dataGridView1.Invalidate();

                bindingNavigatorPositionItem.Text = CurrentPage.ToString();
                bindingNavigatorCountItem.Text = $"of {TotalPages}";
                lblTotalRecords.Text = $"{TotalRecords:N0} record{(TotalRecords == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                LoadError?.Invoke(this, ex);
            }
            finally
            {
                _isLoading = false;
                SetNavEnabled(true);
            }
        }

        private void SetNavEnabled(bool enabled)
        {
            dataGridView1.Enabled = enabled;
            bindingNavigatorMoveFirstItem.Enabled = enabled && CurrentPage > 1;
            bindingNavigatorMovePreviousItem.Enabled = enabled && CurrentPage > 1;
            bindingNavigatorMoveNextItem.Enabled = enabled && CurrentPage < TotalPages;
            bindingNavigatorMoveLastItem.Enabled = enabled && CurrentPage < TotalPages;
        }
    }
}





public enum GridColumnType
    {
        /// <summary>Plain read-only text (the default for auto-generated and AddColumn columns).</summary>
        Text,

        /// <summary>A checkbox, optionally bound to a bool property, optionally with a select-all header.</summary>
        CheckBox,

        /// <summary>A clickable button, invoking a callback with the row's entity.</summary>
        Button
    }

