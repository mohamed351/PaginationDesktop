using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace WindowsFormsApp1
{
    internal class PageLoadResult
    {
        public IList Items { get; set; }
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// Non-generic seam GridPaging talks to. The real work happens in <see cref="PageLoader{T}"/>,
    /// which is instantiated via <c>Activator.CreateInstance(typeof(PageLoader&lt;&gt;).MakeGenericType(entityType))</c>
    /// once the entity type is known (resolved from the chosen DbSet&lt;T&gt; property at runtime).
    /// </summary>
    internal interface IPageLoader
    {
        Task<PageLoadResult> LoadPageAsync(
            object querySource, int skip, int take, string searchTerm,
            string sortColumn, bool sortAscending, IReadOnlyList<FilterCriterion> filters);
    }

    /// <summary>
    /// Fully-typed EF6 query logic (search / filter / sort / count / skip-take), identical in
    /// spirit to a hand-written repository for one specific entity type - except T is only known
    /// at runtime here, supplied via reflection from <see cref="GridPaging"/>.
    /// </summary>
    internal class PageLoader<T> : IPageLoader where T : class
    {
        private static readonly List<PropertyInfo> StringProperties = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();

        private static readonly MethodInfo StringContainsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });
        private static readonly MethodInfo StringStartsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) });
        private static readonly MethodInfo StringEndsWithMethod = typeof(string).GetMethod("EndsWith", new[] { typeof(string) });

        /// <summary>
        /// Used only when no valid sort column is available. SQL Server's OFFSET/FETCH paging
        /// REQUIRES an ORDER BY clause - without one, Skip/Take throws at the database. This
        /// guarantees a sort is always applied, even if the caller never set a default sort
        /// column and no grid column happens to look like a natural key.
        /// </summary>
        private static readonly PropertyInfo FallbackOrderProperty = ResolveFallbackOrderProperty();

        public async Task<PageLoadResult> LoadPageAsync(
            object querySource, int skip, int take, string searchTerm,
            string sortColumn, bool sortAscending, IReadOnlyList<FilterCriterion> filters)
        {
            // querySource is the actual DbSet<T> (or other IQueryable<T>) handed to us as `object`;
            // this cast succeeds because the caller built T to match its runtime type exactly.
            IQueryable<T> query = ((IQueryable<T>)querySource).AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var searchPredicate = BuildSearchPredicate(searchTerm.Trim());
                if (searchPredicate != null)
                    query = query.Where(searchPredicate);
            }

            if (filters != null)
            {
                foreach (var criterion in filters)
                {
                    var predicate = BuildFilterPredicate(criterion);
                    if (predicate != null)
                        query = query.Where(predicate);
                }
            }

            var totalCount = query.Count();

            var resolvedSortColumn = !string.IsNullOrEmpty(sortColumn) && typeof(T).GetProperty(sortColumn) != null
                ? sortColumn
                : FallbackOrderProperty?.Name;

            query = resolvedSortColumn != null
                ? ApplySort(query, resolvedSortColumn, sortAscending)
                : query; // only reachable if T genuinely has zero orderable properties

            var items = await query.Skip(skip).Take(take).ToListAsync().ConfigureAwait(false);

            return new PageLoadResult { Items = items, TotalCount = totalCount };
        }

        /// <summary>
        /// Builds "x =&gt; x.Prop1.Contains(term) || x.Prop2.Contains(term) || ..." across every
        /// string property on T, as an Expression tree so EF6 translates it to SQL (LIKE) instead
        /// of evaluating it client-side.
        /// </summary>
        private static Expression<Func<T, bool>> BuildSearchPredicate(string term)
        {
            if (StringProperties.Count == 0) return null;

            var parameter = Expression.Parameter(typeof(T), "x");
            var termConstant = Expression.Constant(term, typeof(string));

            Expression body = null;
            foreach (var property in StringProperties)
            {
                var propertyAccess = Expression.Property(parameter, property);
                var call = Expression.Call(propertyAccess, StringContainsMethod, termConstant);
                body = body == null ? (Expression)call : Expression.OrElse(body, call);
            }

            return Expression.Lambda<Func<T, bool>>(body, parameter);
        }

        /// <summary>
        /// Builds a single "x =&gt; x.Property OP value" predicate for one FilterCriterion.
        /// Returns null (criterion ignored) if the property doesn't exist or the operator
        /// doesn't apply to its type, rather than throwing.
        /// </summary>
        private static Expression<Func<T, bool>> BuildFilterPredicate(FilterCriterion criterion)
        {
            if (criterion == null || string.IsNullOrEmpty(criterion.PropertyName)) return null;

            var property = typeof(T).GetProperty(criterion.PropertyName);
            if (property == null) return null;

            var parameter = Expression.Parameter(typeof(T), "x");
            var propertyAccess = Expression.Property(parameter, property);

            Expression body;
            switch (criterion.Operator)
            {
                case FilterOperator.Contains:
                case FilterOperator.StartsWith:
                case FilterOperator.EndsWith:
                    if (property.PropertyType != typeof(string)) return null;
                    var method = criterion.Operator == FilterOperator.Contains ? StringContainsMethod
                        : criterion.Operator == FilterOperator.StartsWith ? StringStartsWithMethod
                        : StringEndsWithMethod;
                    var textConstant = Expression.Constant(criterion.Value?.ToString() ?? string.Empty, typeof(string));
                    body = Expression.Call(propertyAccess, method, textConstant);
                    break;

                default:
                    object convertedValue;
                    try
                    {
                        convertedValue = ConvertValue(criterion.Value, property.PropertyType);
                    }
                    catch
                    {
                        return null; // value couldn't be converted to the property's type - ignore this criterion
                    }
                    var valueConstant = Expression.Constant(convertedValue, property.PropertyType);

                    switch (criterion.Operator)
                    {
                        case FilterOperator.Equals: body = Expression.Equal(propertyAccess, valueConstant); break;
                        case FilterOperator.NotEquals: body = Expression.NotEqual(propertyAccess, valueConstant); break;
                        case FilterOperator.GreaterThan: body = Expression.GreaterThan(propertyAccess, valueConstant); break;
                        case FilterOperator.GreaterThanOrEqual: body = Expression.GreaterThanOrEqual(propertyAccess, valueConstant); break;
                        case FilterOperator.LessThan: body = Expression.LessThan(propertyAccess, valueConstant); break;
                        case FilterOperator.LessThanOrEqual: body = Expression.LessThanOrEqual(propertyAccess, valueConstant); break;
                        default: return null;
                    }
                    break;
            }

            return Expression.Lambda<Func<T, bool>>(body, parameter);
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlying.IsEnum) return Enum.Parse(underlying, value.ToString());
            if (underlying == value.GetType()) return value;
            return Convert.ChangeType(value, underlying);
        }

        /// <summary>OrderBy/OrderByDescending built via reflection - no System.Linq.Dynamic.Core dependency.</summary>
        private static IQueryable<T> ApplySort(IQueryable<T> query, string propertyName, bool ascending)
        {
            var property = typeof(T).GetProperty(propertyName);
            if (property == null) return query;

            var parameter = Expression.Parameter(typeof(T), "x");
            var propertyAccess = Expression.MakeMemberAccess(parameter, property);
            var orderByExpression = Expression.Lambda(propertyAccess, parameter);

            var methodName = ascending ? "OrderBy" : "OrderByDescending";
            var resultExpression = Expression.Call(
                typeof(Queryable),
                methodName,
                new[] { typeof(T), property.PropertyType },
                query.Expression,
                Expression.Quote(orderByExpression));

            return query.Provider.CreateQuery<T>(resultExpression);
        }

        private static bool IsOrderableType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                   || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid);
        }

        private static PropertyInfo ResolveFallbackOrderProperty()
        {
            var orderable = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && IsOrderableType(p.PropertyType))
                .ToList();

            // Prefer something that looks like a primary key, since it's a stable, unique sort
            // (paging results can otherwise shift between pages if the fallback column has ties).
            return orderable.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
                ?? orderable.FirstOrDefault(p => p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                ?? orderable.FirstOrDefault();
        }
    }
}
