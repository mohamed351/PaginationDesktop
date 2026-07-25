using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading.Tasks;
using static System.ComponentModel.TypeConverter;
using System.Data.Entity;

namespace WindowsFormsApp1
{
    public class DbContextTypeConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            try
            {
                var names = FindDbContextTypes().Select(t => t.AssemblyQualifiedName).Distinct().ToList();
                return new StandardValuesCollection(names);
            }
            catch
            {
                // Never let a discovery failure take down the whole Properties window -
                // worst case, this property just shows an empty dropdown / free-text box.
                return new StandardValuesCollection(new List<string>());
            }
        }

        internal static IEnumerable<Type> FindDbContextTypes()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .Where(t => t != null && !t.IsAbstract && !t.IsInterface && typeof(DbContext).IsAssignableFrom(t));
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Type.EmptyTypes; }
        }
    }

   
    public class DbSetPropertyConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            try
            {
                var grid = context?.Instance as GridPaging;
                if (grid == null || string.IsNullOrEmpty(grid.DbContextTypeName))
                    return new StandardValuesCollection(new List<string>());

                var contextType = ResolveType(grid.DbContextTypeName);
                if (contextType == null)
                    return new StandardValuesCollection(new List<string>());

                var names = contextType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(IsDbSetProperty)
                    .Select(p => p.Name)
                    .ToList();

                return new StandardValuesCollection(names);
            }
            catch
            {
                return new StandardValuesCollection(new List<string>());
            }
        }

        internal static bool IsDbSetProperty(PropertyInfo property) =>
            property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>);

        internal static Type ResolveType(string assemblyQualifiedName)
        {
            if (string.IsNullOrEmpty(assemblyQualifiedName)) return null;

            var type = Type.GetType(assemblyQualifiedName, throwOnError: false);
            if (type != null) return type;

            // Fall back to scanning loaded assemblies, in case the stored name doesn't
            // round-trip cleanly through Type.GetType (e.g. slightly different build metadata).
            return DbContextTypeConverter.FindDbContextTypes()
                .FirstOrDefault(t => t.AssemblyQualifiedName == assemblyQualifiedName);
        }
    }
}
