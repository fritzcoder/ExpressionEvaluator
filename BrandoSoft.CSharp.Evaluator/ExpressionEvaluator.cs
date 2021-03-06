﻿/*
    MIT License

    Copyright (c) 2016 BrandonLegault

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE. 
 */
namespace BrandoSoft.CSharp.Evaluator
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using Mono.CSharp;

    /// <summary>
    /// Provides an API into a runtime C# compiler and evaluator.
    /// </summary>
    public sealed class RuntimeEvaluator
        : IExpressionEvaluator, IDisposable
    {
        #region Fields

        /// <summary>
        /// Our internal list of imported namespaces
        /// </summary>
        private readonly List<string> _importedNamespaces;

        /// <summary>
        /// The object that we will call into to perform the expression evaluation
        /// </summary>
        private Evaluator _evaluator;

        /// <summary>
        /// Our Mono C# Compiler
        /// </summary>
        private CompilerContext _compiler;

        /// <summary>
        /// This is used for any expression evaluation errors.
        /// </summary>
        private StringBuilder _errors;

        /// <summary>
        /// Passed to the C# compiler as an output stream.
        /// </summary>
        private StringWriter _errorWriter;

        #endregion


        #region Constructor
        /// <summary>
        /// Construct a default expression evaluator
        /// </summary>
        public RuntimeEvaluator()
        {
            this._importedNamespaces = new List<string>();

            this._errors = new StringBuilder();

            this._errorWriter = new StringWriter(this._errors);

            this._compiler = new CompilerContext(new CompilerSettings(), new StreamReportPrinter(this._errorWriter));
            this._evaluator = new Evaluator(this._compiler);

            //We don't include any statements to begin with (usings etc...)
            this._evaluator.Run("");


        }
        #endregion

        /*****/

        #region IExpressionEvaluator Implementation

        /// <summary>
        /// Reports errors, if any, in the most-recently executed operation.
        /// </summary>
        public string LastOperationErrors => this._errors.ToString();

        /// <summary>
        /// A list of assemblies that the evaluator is currently referencing.
        /// </summary>
        public IReadOnlyCollection<string> ReferencedAssemblies => this._compiler.Settings.AssemblyReferences;

        /// <summary>
        /// A list of using directives that've been added to the evaluator.
        /// </summary>
        public IReadOnlyCollection<string> ImportedNamespaces => this._importedNamespaces;

        /// <summary>
        /// Imports a collection of namespaces. 
        /// </summary>
        /// <param name="usingDirectives"></param>
        public void ImportNamespaces(IEnumerable<string> usingDirectives)
        {
            this._errors.Clear();
            var regex = new Regex("[ ]{2,}", RegexOptions.None);

            //Make sure we have a directives list.
            var directives = (usingDirectives ?? Enumerable.Empty<string>()).ToList();

            foreach (var directive in string.Join("", directives).Split(';'))
            {
                var normalizedDirective = directive.Trim() + ";";

                //Add the using statement to the front if it isn't already there.
                if (!string.IsNullOrEmpty(normalizedDirective) && !normalizedDirective.StartsWith("using"))
                {
                    normalizedDirective = "using " + normalizedDirective;
                }

                //Replace multiple spaces in the directive with a single space.
                //Ensures that we can add it properly to our internal imports list.
                normalizedDirective = regex.Replace(normalizedDirective, " ");

                if ( !this._importedNamespaces.Contains(normalizedDirective) )
                {
                    this.Evaluate(normalizedDirective);
                    this._importedNamespaces.Add(normalizedDirective);
                }
            }
        }

        /// <summary>
        /// Makes this IExpressionEvaluator aware of an instantiated .NET object so its properties are queryable
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instance">The object we plan on evaluating</param>
        /// <param name="instanceName">The instance/alias name of the object.</param>
        public void AddInstancedObject<T>(T instance, string instanceName)
        {
            instanceName = instanceName.Trim();

            if (instanceName == "this")
                throw new ArgumentException("The passed instance object cannot be aliased to 'this'.", nameof(instanceName));

            if (instanceName.Contains(" "))
                throw new ArgumentException("Your instance name cannot contain spaces.", nameof(instanceName));
            this._errors.Clear();

            try
            {
                object result;
                bool results;

                // Make the evaluator create an internal variable with the instance name passed, set to null.
                var constructor = $"{instance.GetType().FullName} {instanceName} = null;";
                this._evaluator.Evaluate(constructor, out result, out results);
                
                FieldInfo fieldInfo = typeof(Evaluator).GetField("fields", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fieldInfo != null)
                {
                    var fields = (Dictionary<string, Tuple<FieldSpec, FieldInfo>>)fieldInfo.GetValue(this._evaluator);

                    //Now replace that private field with the instance we were given above.
                    fields[instanceName].Item2.SetValue(this._evaluator, instance);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"{this._errors}", ex);
            }
        }

        /// <summary>
        /// Adds a reference to a .NET assembly to this expression evaluator
        /// </summary>
        /// <param name="fullAssemblyName"></param>
        public void AddAssemblyReference(string fullAssemblyName)
        {
            try
            {
                if (!this._compiler.Settings.AssemblyReferences.Contains(fullAssemblyName))
                {
                    this._compiler.Settings.AssemblyReferences.Add(fullAssemblyName);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"{this._errors}", ex);
            }
        }

        /// <summary>
        /// Evaluates or applies any valid C# expression and returns its output, if any.
        /// </summary>
        /// <param name="expression"></param>
        /// <returns></returns>
        public string Evaluate(string expression)
        {
            try
            {
                object result;
                bool results;

                this._evaluator.Evaluate(expression, out result, out results);

                if (results)
                {
                    return result.ToString();
                }

                if (this._errors.Length > 0)
                {
                    return this._errors.ToString();
                }
            }
            catch (Exception ex)
            {
                return $"{ex.Message} {this._errors}";
            }
            finally
            {
                this._errors.Clear();
            }
            return "";
        }

        #endregion
        #region IDisposable Implementation

        /// <summary>
        /// Disposes our IExpressionEvaluator.
        /// </summary>
        public void Dispose()
        {
            try
            {
                this._errorWriter.Dispose();
            }
            catch (Exception)
            {
            }
        }
        #endregion
    }
}
