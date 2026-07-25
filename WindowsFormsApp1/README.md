# GridPaging

One WinForms `UserControl` for .NET Framework 4.8: a `DataGridView` (Virtual Mode), a search
box, and a `BindingNavigator`-style paging strip - for **any** EF6 `DbContext` / `DbSet<T>`,
picked entirely from the **Properties window**, no code required.

## Using it (design time)

1. Drag `GridPaging` onto a form (it's a plain, non-generic control, so it shows up in the
   Toolbox and can be dropped straight onto a design surface).
2. In the Properties window, under **Data**:
   - **DbContextTypeName** - dropdown of every `DbContext`-derived type found in your project.
   - **DbSetPropertyName** - once a context is chosen, dropdown of its `DbSet<T>` properties.
     Picking one of these is how you choose the entity/"class for DbSet" to page through.
3. Run the app. Columns and the cross-column search filter are generated automatically via
   reflection over the entity type - nothing else to configure.

## Using it (in code)

```csharp
var grid = new GridPaging { Dock = DockStyle.Fill };
Controls.Add(grid);

grid.DbContextTypeName = typeof(SampleDbContext).AssemblyQualifiedName;
grid.DbSetPropertyName = nameof(SampleDbContext.Customers);
grid.Bind(); // only needed if you set the properties after the control has already loaded -
             // Bind() runs automatically on Load if the properties were already set (e.g. via
             // the designer, or set in code before the control loads).
```

## Files

```
Controls/
  GridPaging.cs                 UI logic: reflection, paging, search, virtual-mode rendering
  GridPaging.Designer.cs        InitializeComponent (see note below)
  PageLoader.cs                 Per-entity EF6 query logic (search/sort/count/skip-take),
                                 instantiated via reflection so GridPaging can stay non-generic
  DesignTimeConverters.cs       TypeConverters powering the two Properties-window dropdowns
Sample/
  Customer.cs                   Example entity - nothing special, any entity works the same way
  SampleDbContext.cs             Plain EF6 DbContext with a DbSet<Customer>
  ExampleForm.cs                 Wiring example (setting the two properties in code)
  App.config                     Connection string + EF6 provider registration
```

## How the two dropdowns work

- **DbContextTypeName** uses `DbContextTypeConverter`, a `StringConverter` whose
  `GetStandardValues` scans `AppDomain.CurrentDomain.GetAssemblies()` for every non-abstract
  type assignable to `System.Data.Entity.DbContext`.
- **DbSetPropertyName** uses `DbSetPropertyConverter`, which reads the sibling
  `DbContextTypeName` off the component instance being edited (`context.Instance`), resolves
  that type, and lists its properties whose type is `DbSet<T>` for some `T`.
- Both converters set `GetStandardValuesExclusive = false`, so free-typed values still work -
  the dropdown is a convenience, not a hard restriction - and both wrap their logic in
  try/catch so a discovery failure only produces an empty dropdown, never breaks the rest of
  the Properties window.
- **Practical note**: a brand-new `DbContext` class only shows up in the dropdown once the
  project has been **built at least once** (the converter can only see types in assemblies
  that are actually loaded) - if you don't want to wait, you can always type the
  assembly-qualified name into `DbContextTypeName` directly.
- Deliberately **no dependency on `ITypeDiscoveryService`/`System.Design.dll`** - that service
  needs an extra manual assembly reference most WinForms projects don't have by default, and a
  missing reference means the project fails to build, which is the most common reason these
  properties disappear from the Properties window entirely (Visual Studio just shows the last
  successfully built version of the control).

## How paging/search/columns work at runtime

**Resolving the entity type** - `Bind()` (called automatically on `Load`, or manually) reflects
`DbSetPropertyName` off `DbContextTypeName`, reads its generic argument (`DbSet<TEntity>` →
`TEntity`), and builds an `IPageLoader` via
`Activator.CreateInstance(typeof(PageLoader<>).MakeGenericType(entityType))`. `PageLoader<T>` is
fully generic/typed internally - identical in spirit to a hand-written repository - it's just
instantiated at runtime because `T` isn't known until then.

**Paging** - each page turn creates a fresh `DbContext` (`Activator.CreateInstance(_contextType)`),
reads the chosen `DbSet<T>` property off it, and calls into `PageLoader<T>`, which applies the
search predicate, calls `.Count()`, applies sort, then `.Skip(...).Take(pageSize)`. Nothing is
enumerated until the final `ToListAsync()`, so EF6 folds it into one SQL statement translated to
`OFFSET ... FETCH NEXT ... ROWS ONLY` on SQL Server 2012+. The context is disposed right after
that page's rows are materialized.

**Automatic columns** - the first time `Bind()` runs, if no columns were added manually,
`GridPaging` reflects over the entity type's public properties and adds a column for each
"simple" one (string, numeric, bool, DateTime, enum, Guid) - navigation/collection properties
are skipped automatically, and anything marked `[NotMapped]` is skipped too. Headers are
auto-humanized (`CustomerId` → `Customer Id`). Cell values are read via a compiled
`Func<object, object>` accessor per column (built with an `Expression.Convert` from `object` to
the runtime entity type), not per-cell reflection.

**Automatic search** - `PageLoader<T>` finds every `string` property on `T` and, when you type
something and press Enter, builds
`x => x.Prop1.Contains(term) || x.Prop2.Contains(term) || ...` as an **Expression tree** (not a
compiled delegate), so EF6 translates it into SQL (`LIKE '%term%'`) rather than pulling every
row into memory to filter client-side.

**The BindingNavigator is chrome, not data-bound** - it's *not* attached to a `BindingSource`
(that pattern assumes the full dataset is loaded into memory, which defeats the point of
server-side paging). Its `Move*` buttons, position textbox, and count label are wired to custom
handlers that just trigger a fresh page load with the right page number.

## Customizing columns

Call `AddColumn`/`AddCheckBoxColumn`/`AddButtonColumn` (with `DbContextTypeName`/`DbSetPropertyName`
already set) any number of times **before** `Bind()` - as soon as one column exists, automatic
generation is skipped and you have full manual control:

```csharp
grid.DbContextTypeName = typeof(SampleDbContext).AssemblyQualifiedName;
grid.DbSetPropertyName = nameof(SampleDbContext.Customers);
grid.AddColumn(nameof(Customer.CustomerId), "ID", 60);
grid.AddColumn(nameof(Customer.Name), "Full Name", 220);
grid.AddColumn(nameof(Customer.CreatedOn), "Joined", 120, "yyyy-MM-dd");
grid.Bind();
```

If you leave column setup to auto-generation, `bool` properties on the entity are picked up as
plain (non-select-all) checkbox columns automatically - no configuration needed.

### Column types

Three column types are available:

- **Text** (default) - `AddColumn(propertyName, headerText, width, format)`.
- **Checkbox** - `AddCheckBoxColumn(propertyName, headerText, width, showSelectAllHeader)`.
  - Pass a `propertyName` to bind it to a `bool` **or numeric/enum** property (edits apply to
    the in-memory row object only, not written back to the database - this is a display grid,
    not an editor). Numeric properties are treated as the classic "0/1 flag" pattern: any
    non-zero value displays as checked, `0` as unchecked; checking/unchecking writes `1`/`0`
    back (via `Convert.ChangeType`, so it lands as whatever numeric type the property actually
    is - `int`, `byte`, `long`, etc.). Enum properties work the same way, using the enum's
    underlying numeric value. A read-only property (no public setter) is still displayed
    correctly, just not editable.
  - Leave `propertyName` null for a pure "selection" column not tied to any entity property;
    read which rows are checked with `grid.GetSelectedItems()`.
  - `showSelectAllHeader` (default `true`) adds a checkbox in the column header that toggles
    every row **currently on screen**. It only applies to the loaded page, by design - selecting
    across every page would mean materializing the entire result set, defeating the point of
    server-side paging.
  - The column explicitly sets `ValueType = typeof(bool)` (plus `TrueValue`/`FalseValue`) -
    without this, `DataGridViewCheckBoxColumn` in Virtual Mode can fail to interpret the value
    handed back from `CellValueNeeded` and shows every row unchecked regardless of the real
    data, until a cell is manually toggled once. That's the actual data now, immediately.
  - Checkbox cells are `ReadOnly` at the `DataGridView` level, and toggling is handled entirely
    by this control's own `CellClick`/`KeyDown` (Space bar) logic instead of relying on
    `DataGridView`'s built-in click-to-edit-checkbox pipeline. That pipeline is unreliable in
    Virtual Mode - clicking a checkbox that isn't already the selected cell can silently fail to
    register the very first click, or need it twice. `ReadOnly` here only blocks the framework's
    own editing mechanism, not clicks/key presses, so a checkbox column (including one bound to
    a writable property) is still fully interactive - just through code we control directly.
- **Button** - `AddButtonColumn(buttonText, onClick, headerText, width)`, where `onClick` is an
  `Action<object>` invoked with the row's entity object when its button is clicked.
- **Empty (unbound text)** - `AddEmptyColumn(headerText, width, textProvider)`, for a column
  that isn't tied to any entity property at all:
  - Pass `textProvider` (`Func<object, string>`) to compute each cell's text from the row's
    entity every time it's rendered - e.g. a derived/combined value like
    `c => $"{((Customer)c).Name} ({((Customer)c).Country})"`.
  - Leave `textProvider` null and cells default to blank; set them yourself, cell by cell, with
    `SetCellText(columnName, rowIndex, text)` (`rowIndex` is 0-based, relative to the page
    currently on screen - like the rest of this control's per-row state, it's cleared on the
    next page/search/sort/filter reload).

```csharp
grid.AddColumn(nameof(Customer.Name), "Name", 200);
grid.AddCheckBoxColumn(propertyName: null, headerText: "", width: 30, showSelectAllHeader: true);
grid.AddCheckBoxColumn(nameof(Customer.IsActive), "Active");
grid.AddButtonColumn("Delete", customer => DeleteCustomer((Customer)customer));
var statusColumn = grid.AddColumn(null, "Status"); // shortcut for AddEmptyColumn("Status") - blank until SetCellText is called
grid.Bind();

// later, e.g. after some async check per row - by column name, or by row/column index:
grid.SetCellText(statusColumn.Name, rowIndex: 0, "OK");
grid.SetCellText(rowIndex: 1, columnIndex: 5, "Failed");

// or rows checked via the unbound selection column:
var selected = grid.GetSelectedItems();
```

### Reordering columns - `SetColumnIndex`

Moves an already-added column to a new visual position, identified by its header text (or, if
nothing matches, by its column/property name). Uses `DataGridViewColumn.DisplayIndex`
internally, so it's purely a visual reorder - nothing else (accessors, checkbox/button/text
bookkeeping, all keyed by column name) is affected:

```csharp
grid.SetColumnIndex("Status", 0); // move the Status column to the far left
```

### Hiding/showing columns - `HideColumn` / `ShowColumn` / `SetColumnVisible`

Same lookup rule as `SetColumnIndex` (header text first, then column/property name):

```csharp
grid.HideColumn("Email");     // same as grid.SetColumnVisible("Email", false)
grid.ShowColumn("Email");     // same as grid.SetColumnVisible("Email", true)
```

The column stays fully configured (accessor, checkbox/button bookkeeping, etc.) while hidden -
this just toggles `DataGridViewColumn.Visible`, so showing it again picks up right where it
left off, including anything you set via `SetCellText` on an unbound column.

### Renaming a column's identifier - `RenameColumn`

Every column has an internal `Name` (what `RemoveColumn`, `SetColumnType`, and `SetCellText`
look it up by) - separate from its visible header text and from the entity property it's bound
to (which never changes). This matters most for a column created without an explicit name -
`AddButtonColumn` and an unbound `AddCheckBoxColumn` both generate a GUID-based one - if you want
a friendlier identifier to reference later instead of hanging onto the returned `DataGridViewColumn`:

```csharp
var selectColumn = grid.AddCheckBoxColumn(propertyName: null, showSelectAllHeader: true);
grid.RenameColumn(selectColumn.Name, "Select"); // was a GUID-based name; now referenceable as "Select"
grid.SetColumnIndex("Select", 0);
grid.RemoveColumn("Select"); // ...or referenced later for removal, etc.
```

### Getting the IDs of checked rows

**Don't bind a checkbox column directly to your ID/primary-key property** (e.g.
`grid.SetColumnType("Id", GridColumnType.CheckBox)`) if what you actually want is a "select
these rows" checkbox - checking/unchecking it would overwrite that row's real ID (to `1`/`0`)
in memory, since the checkbox and the ID are now the same underlying property. Also, `Id`-like
columns are almost always the grid's default sort column, and a checkbox column is
`SortMode.NotSortable` - clicking anywhere in its header (including the select-all checkbox
area) still fires a header-click event, and older versions of this control could crash trying to
show a sort glyph on a non-sortable column. That's now guarded against everywhere it could
happen, but binding a checkbox straight to your ID column is still not what you want semantically.

Instead, add a separate **unbound** selection checkbox column, and use `GetSelectedIds` to read
back the real IDs of whichever rows are checked:

```csharp
grid.AddCheckBoxColumn(propertyName: null, headerText: "", width: 30, showSelectAllHeader: true);
grid.AddColumn(nameof(Customer.CustomerId), "ID", 60); // ID stays a normal, untouched text column
grid.Bind();

// later:
var ids = grid.GetSelectedIds(nameof(Customer.CustomerId)); // IDs of every checked row on screen
```

`GetSelectedIds` also works with a checkbox bound to a real bool/numeric property (e.g.
`IsActive`) if that's genuinely what "checked" means for your case - it reads whichever
checkbox column reflects "checked" (the unbound selection state, or the live property value for
a bound one) and maps each selected row through the ID property you give it.

### Changing a column's type after the fact - `SetColumnType`

If a column already exists (auto-generated, or added via `AddColumn`), you can convert it to a
different type in place with `SetColumnType`, instead of removing and re-adding it yourself.
This is the same mechanism auto-generation itself uses under the hood for `bool` properties.

```csharp
// Auto-generation already made a text column for IsActive - promote it to a real checkbox
// with a select-all header, keeping its existing header text/width unless overridden:
grid.SetColumnType(nameof(Customer.IsActive), GridColumnType.CheckBox);

// Turn a column into a button column instead (buttonText/onButtonClick are required for Button):
grid.SetColumnType(
    "RowActions", GridColumnType.Button,
    buttonText: "Delete",
    onButtonClick: customer => DeleteCustomer((Customer)customer));
```

Any of `headerText`/`width`/`format` you omit (or leave `0`/`null`) are carried over from the
existing column automatically; if no column exists yet for that name, one is added fresh at the
end of the grid using sensible defaults. `RemoveColumn(name)` removes a column (and its
bookkeeping) entirely, if you just want it gone.

## Default sort column

SQL Server's `OFFSET ... FETCH NEXT` paging **requires an `ORDER BY` clause** - without one,
the very first page load throws at the database. `GridPaging` handles this two ways:

1. **`DefaultSortColumn`** (optional) - set it to a property name to control what the grid sorts
   by before any column header has been clicked. Leave it blank and the **first data-bound
   column** is used automatically. `DefaultSortAscending` (default `true`) controls the direction.
2. As a last-resort safety net, if neither of those resolves to a real property on the entity
   (e.g. `DefaultSortColumn` has a typo), `PageLoader<T>` falls back to ordering by whatever
   looks like a primary key (a property named `Id`, or ending in `Id`), or failing that, the
   first orderable property it can find - so a valid `ORDER BY` is always present no matter what.

```csharp
grid.DefaultSortColumn = nameof(Customer.CreatedOn);
grid.DefaultSortAscending = false; // newest first
```

## Runtime filtering

Call `SetFilters(...)` at any time (from a button click, another control's event, etc.) to
filter the underlying query server-side. Filters are combined with AND, and with whatever's
typed in the search box (also AND):

```csharp
grid.SetFilters(
    new FilterCriterion(nameof(Customer.Country), FilterOperator.Equals, "Egypt"),
    new FilterCriterion(nameof(Customer.CreatedOn), FilterOperator.GreaterThanOrEqual, new DateTime(2026, 1, 1)));

// ... later, to remove them:
grid.ClearFilters();
```

`FilterOperator` supports `Equals`, `NotEquals`, `Contains`, `StartsWith`, `EndsWith`,
`GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual`. `Contains`/`StartsWith`/`EndsWith`
only apply to `string` properties; values are converted to the property's actual type
(including nullable and enum properties). If a criterion's property name doesn't exist, or its
operator doesn't make sense for the property's type, that one criterion is silently skipped
rather than throwing - so you can pass a broad, generic set of filter options without worrying
about which ones apply to a particular entity.

## Troubleshooting: properties not showing up in the Properties window

This almost always means the project didn't actually build successfully with the new files in
place, so Visual Studio is showing an older/incomplete version of the control. Check, in order:

1. **All four Controls files are added to the project**, not just present on disk:
   `GridPaging.cs`, `GridPaging.Designer.cs`, `PageLoader.cs`, `DesignTimeConverters.cs`. If you
   copied files in via Explorer instead of "Add > Existing Item", they may be on disk but not
   part of the `.csproj` - right-click the project > *Show All Files*, and if they appear
   greyed-out, right-click > *Include In Project*.
2. **Build > Rebuild Solution** and check the **Error List** for red errors (View > Error List).
   A single compile error anywhere in the project is enough to make the designer fall back to
   stale metadata for every control in it.
3. **Close and reopen the form's designer tab** (or restart Visual Studio) after a successful
   rebuild - the designer process caches a control's metadata for the session and doesn't
   always pick up changes to already-open designer surfaces automatically.
4. Confirm the control on your form is actually the current `GridPaging` type (not a leftover
   reference to an older build, e.g. from a different output path or NuGet-packaged copy).

If PageSize also isn't showing (a plain `int` property with no custom converter), that confirms
the whole control failed to load in the designer - fix the build first and the rest should
follow automatically.

## Required NuGet package

- `EntityFramework` (6.x), for `DbContext`, `DbSet<T>`, `AsNoTracking` (applied automatically
  inside `PageLoader<T>`, since this is a read-only display grid), and `ToListAsync`. SQL
  Server connectivity comes from EF6's default `System.Data.SqlClient` provider.
