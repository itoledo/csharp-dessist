﻿/*
 * 2012-2015 Ted Spence, http://tedspence.com
 * License: http://www.apache.org/licenses/LICENSE-2.0 
 * Home page: https://github.com/tspence/csharp-dessist
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.IO;
using System.Text.RegularExpressions;

namespace csharp_dessist
{
    public enum SqlCompatibilityType { SQL2008, SQL2005 };

    public class Program
    {
        public static SqlCompatibilityType gSqlMode = SqlCompatibilityType.SQL2008;

        /// <summary>
        /// Attempt to read an SSIS package and produce a meaningful C# program
        /// </summary>
        /// <param name="ssis_filename"></param>
        /// <param name="output_folder"></param>
        [Wrap(Name="DESSIST", Description="CSHARP-DESSIST - Read in data from an SSIS package and produce an equivalent C# program using .NET 4.0.")]
        public static void ParseSsisPackage(string ssis_filename, string output_folder, SqlCompatibilityType SqlMode = SqlCompatibilityType.SQL2008, bool UseSqlSMO = true)
        {
            XmlReaderSettings set = new XmlReaderSettings();
            set.IgnoreWhitespace = true;
            SsisObject o = new SsisObject();
            gSqlMode = SqlMode;

            // Make sure output folder exists
            Directory.CreateDirectory(output_folder);

            // Set the appropriate flag for SMO usage
            ProjectWriter.UseSqlServerManagementObjects = UseSqlSMO;

            // TODO: Should read the dtproj file instead of the dtsx file, then produce multiple classes, one for each .DTSX file

            // Read in the file, one element at a time
            XmlDocument xd = new XmlDocument();
            xd.Load(ssis_filename);
            ReadObject(xd.DocumentElement, o);

            // Now let's produce something meaningful out of this mess!
            ProduceSsisDotNetPackage(Path.GetFileNameWithoutExtension(ssis_filename), o, output_folder);
        }

        #region Write the SSIS package to a C# folder
        /// <summary>
        /// Produce C# files that replicate the functionality of an SSIS package
        /// </summary>
        /// <param name="o"></param>
        private static void ProduceSsisDotNetPackage(string projectname, SsisObject o, string output_folder)
        {
            ProjectWriter.AppFolder = output_folder;

            // First find all the connection strings and write them to an app.config file
            var connstrings = from SsisObject c in o.Children where c.DtsObjectType == "DTS:ConnectionManager" select c;
            ConnectionWriter.WriteAppConfig(connstrings, Path.Combine(output_folder, "app.config"));

            // Next, write all the executable functions to the main file
            var functions = from SsisObject c in o.Children where c.DtsObjectType == "DTS:Executable" select c;
            if (!functions.Any()) {
                var executables = from SsisObject c in o.Children where c.DtsObjectType == "DTS:Executables" select c;
                List<SsisObject> flist = new List<SsisObject>();
                foreach (var exec in executables) {
                    flist.AddRange(from e in exec.Children where e.DtsObjectType == "DTS:Executable" select e);
                }
                if (flist.Count == 0) {
                    Console.WriteLine("No functions ('DTS:Executable') objects found in the specified file.");
                    return;
                }
                functions = flist;
            }
            List<SsisObject> vars = new List<SsisObject>();
            var variables = from SsisObject c in o.Children where c.DtsObjectType == "DTS:Variable" select c;
            vars.AddRange(variables);

            var containerVariables = o.Children.Where(c => c.DtsObjectType == "DTS:Variables");
            foreach(var cont in containerVariables)
            {
                var newvars = from SsisObject c in cont.Children where c.DtsObjectType == "DTS:Variable" select c;
                vars.AddRange(newvars);
            }
            WriteProgram(vars, functions, Path.Combine(output_folder, "program.cs"), projectname);

            // Next write the resources and the project file
            ProjectWriter.WriteResourceAndProjectFile(output_folder, projectname);
        }

        /// <summary>
        /// Write a program file that has all the major executable instructions as functions
        /// </summary>
        /// <param name="variables"></param>
        /// <param name="functions"></param>
        /// <param name="p"></param>
        private static void WriteProgram(IEnumerable<SsisObject> variables, IEnumerable<SsisObject> functions, string filename, string appname)
        {
            using (SourceWriter.SourceFileStream = new StreamWriter(filename, false, Encoding.UTF8)) {

                string smo_using = "";
                string tableparamstatic = "";

                // Are we using SMO mode?
                if (ProjectWriter.UseSqlServerManagementObjects) {
                    smo_using = Resource1.SqlSmoUsingTemplate;
                }

                // Are we using SQL 2008 mode?
                if (gSqlMode == SqlCompatibilityType.SQL2008) {
                    tableparamstatic = Resource1.TableParameterStaticTemplate;
                }

                // Write the header in one fell swoop
                SourceWriter.Write(
                    Resource1.ProgramHeaderTemplate
                    .Replace("@@USINGSQLSMO@@", smo_using)
                    .Replace("@@NAMESPACE@@", appname)
                    .Replace("@@TABLEPARAMSTATIC@@", tableparamstatic)
                    .Replace("@@MAINFUNCTION@@", functions.FirstOrDefault().GetFunctionName())
                    );

                // Write each variable out as if it's a global
                SourceWriter.WriteLine(@"#region Global Variables");
                foreach (SsisObject v in variables) {
                    v.EmitVariable("        ", true);
                }
                SourceWriter.WriteLine(@"#endregion");
                SourceWriter.WriteLine();
                SourceWriter.WriteLine();

                
                // Write each executable out as a function
                SourceWriter.WriteLine(@"#region SSIS Code");
                foreach (SsisObject v in functions) {
                    v.EmitFunction("        ", new List<ProgramVariable>());
                }
                SourceWriter.WriteLine(Resource1.ProgramFooterTemplate);
            }
        }
        #endregion

        #region Read in an SSIS DTSX file
        /// <summary>
        /// Recursive read function
        /// </summary>
        /// <param name="xr"></param>
        /// <param name="o"></param>
        private static void ReadObject(XmlElement el, SsisObject o)
        {
            // Read in the object name
            o.DtsObjectType = el.Name;

            // Read in attributes
            foreach (XmlAttribute xa in el.Attributes) {
                o.Attributes.Add(xa.Name, xa.Value);
            }

            // Iterate through all children of this element
            foreach (XmlNode child in el.ChildNodes) {

                // For child elements
                if (child is XmlElement) {
                    XmlElement child_el = child as XmlElement;

                    // Read in a DTS Property
                    if (child.Name == "DTS:Property" || child.Name == "DTS:PropertyExpression") {
                        ReadDtsProperty(child_el, o);

                        // Everything else is a sub-object
                    } else {
                        SsisObject child_obj = new SsisObject();
                        ReadObject(child_el, child_obj);
                        child_obj.Parent = o;
                        o.Children.Add(child_obj);
                    }
                } else if (child is XmlText) {
                    o.ContentValue = child.InnerText;
                } else if (child is XmlCDataSection) {
                    o.ContentValue = child.InnerText;
                } else {
                    Console.WriteLine("Help");
                }
            }

            if (o.DtsObjectName == null && o.Attributes.ContainsKey("DTS:ObjectName"))
                o.DtsObjectName = o.Attributes["DTS:ObjectName"];
        }

        /// <summary>
        /// Read in a DTS property from the XML stream
        /// </summary>
        /// <param name="xr"></param>
        /// <param name="o"></param>
        private static void ReadDtsProperty(XmlElement el, SsisObject o)
        {
            string prop_name = null;

            // Read all the attributes
            foreach (XmlAttribute xa in el.Attributes) {
                if (String.Equals(xa.Name, "DTS:Name", StringComparison.CurrentCultureIgnoreCase)) {
                    prop_name = xa.Value;
                    break;
                }
            }

            // Set the property
            o.SetProperty(prop_name, el.InnerText);
        }
        #endregion
    }
}
