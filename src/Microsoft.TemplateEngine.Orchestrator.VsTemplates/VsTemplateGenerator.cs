﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.TemplateEngine.Abstractions;

namespace Microsoft.TemplateEngine.Orchestrator.VsTemplates
{
    public class VsTemplateGenerator : IGenerator
    {
        public string Name => "VS Templates";

        private void RecurseContent(XElement parentNode, string sourcePath, string targetPath, string defaultName, string useName, IDictionary<string, string> fileMap, IList<string> copyOnly)
        {
            IEnumerable<XElement> projects = parentNode.Elements().Where(x => x.Name.LocalName == "Project");
            IEnumerable<XElement> items = parentNode.Elements().Where(y => y.Name.LocalName == "ProjectItem");
            IEnumerable<XElement> folders = parentNode.Elements().Where(y => y.Name.LocalName == "Folder");

            foreach (XElement project in projects)
            {
                string sourceName = project.Attributes().First(x => x.Name.LocalName == "File").Value;
                string targetFileName = project.Attributes().FirstOrDefault(x => x.Name.LocalName == "TargetFileName")?.Value ?? sourceName;
                bool processReplacements = bool.Parse(project.Attributes().FirstOrDefault(x => x.Name.LocalName == "ReplaceParameters")?.Value ?? "False");

                targetFileName = targetFileName.Replace(defaultName, useName);

                if (!processReplacements)
                {
                    copyOnly.Add(sourceName);
                }

                fileMap[sourcePath + sourceName] = targetPath + targetFileName;
                RecurseContent(project, sourcePath, targetPath, defaultName, useName, fileMap, copyOnly);
            }

            foreach (XElement file in items)
            {
                string sourceName = file.Value;
                string targetFileName = file.Attributes().FirstOrDefault(x => x.Name.LocalName == "TargetFileName")?.Value ?? sourceName;
                bool processReplacements = bool.Parse(file.Attributes().FirstOrDefault(x => x.Name.LocalName == "ReplaceParameters")?.Value ?? "False");

                targetFileName = targetFileName.Replace(defaultName, useName);
                targetFileName = targetFileName.Replace("$fileinputname$", Path.GetFileNameWithoutExtension(useName));

                if (!processReplacements)
                {
                    copyOnly.Add(sourceName);
                }

                fileMap[sourceName] = targetPath + targetFileName;
            }

            foreach (XElement folder in folders)
            {
                string sourceName = folder.Attributes().FirstOrDefault(x => x.Name.LocalName == "Name")?.Value;
                string targetName = folder.Attributes().FirstOrDefault(x => x.Name.LocalName == "TargetFolderName")?.Value;
                RecurseContent(folder, sourcePath + sourceName + "\\", targetPath + targetName + "\\", defaultName, useName, fileMap, copyOnly);
            }
        }

        public Task Create(ITemplate template, IParameterSet parameters)
        {
            ProcessParameters(parameters);
            VsTemplate tmplt = (VsTemplate)template;
            XElement templateContent = tmplt.VsTemplateFile.Root.Descendants().First(x => x.Name.LocalName == "TemplateContent");

            ParameterSet p = (ParameterSet)parameters;

            foreach (CustomParameter parameter in tmplt.CustomParameters)
            {
                ITemplateParameter target;
                if (p.TryGetParameter(parameter.Name, out target))
                {
                    if (!p.ParameterValues.ContainsKey(target))
                    {
                        p.ParameterValues[target] = parameter.DefaultValue;
                    }
                }
            }

            ITemplateParameter projectNameParameter;
            p.TryGetParameter("projectname", out projectNameParameter);

            Dictionary<string, string> fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> copyOnly = new List<string>();
            RecurseContent(templateContent, "", "", tmplt.DefaultName, parameters.ParameterValues[projectNameParameter], fileMap, copyOnly);

            VsTemplateOrchestrator o = new VsTemplateOrchestrator();
            o.Run(new VsTemplateGlobalRunSpec(parameters, fileMap, copyOnly), tmplt.SourceFile.Parent, Directory.GetCurrentDirectory());
            return Task.FromResult(true);
        }

        public IParameterSet GetParametersForTemplate(ITemplate template)
        {
            ParameterSet result = new ParameterSet();
            VsTemplate tmplt = (VsTemplate)template;

            foreach (CustomParameter param in tmplt.CustomParameters)
            {
                result.AddParameter(new Parameter(param.Name, TemplateParameterPriority.Optional, "string", defaultValue: param.DefaultValue));
            }

            return result;
        }

        public IEnumerable<ITemplate> GetTemplatesFromSource(IConfiguredTemplateSource source)
        {
            using (IDisposable<ITemplateSourceFolder> root = source.Root)
            {
                return GetTemplatesFromDir(source, root.Value).ToList();
            }
        }

        public bool TryGetTemplateFromSource(IConfiguredTemplateSource target, string name, out ITemplate template)
        {
            template = GetTemplatesFromSource(target).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return template != null;
        }

        private IEnumerable<ITemplate> GetTemplatesFromDir(IConfiguredTemplateSource source, ITemplateSourceFolder folder)
        {
            foreach (ITemplateSourceEntry entry in folder.Children)
            {
                if (entry.Kind == TemplateSourceEntryKind.File && entry.FullPath.EndsWith(".vstemplate"))
                {
                    VsTemplate tmp = null;
                    try
                    {
                        tmp = new VsTemplate((ITemplateSourceFile)entry, source, this);
                    }
                    catch
                    {
                    }

                    if (tmp != null)
                    {
                        yield return tmp;
                    }
                }
                else if (entry.Kind == TemplateSourceEntryKind.Folder)
                {
                    foreach (ITemplate template in GetTemplatesFromDir(source, (ITemplateSourceFolder)entry))
                    {
                        yield return template;
                    }
                }
            }
        }

        private static void ProcessParameters(IParameterSet parameters)
        {
            ParameterSet p = (ParameterSet)parameters;
            ITemplateParameter safeProjectName = new Parameter("safeprojectname", TemplateParameterPriority.Required, "string");
            p.AddParameter(safeProjectName);
            ITemplateParameter itemName = new Parameter("itemname", TemplateParameterPriority.Required, "string");
            p.AddParameter(itemName);
            ITemplateParameter safeItemName = new Parameter("safeitemname", TemplateParameterPriority.Required, "string");
            p.AddParameter(safeItemName);
            ITemplateParameter fileInputName = new Parameter("fileinputname", TemplateParameterPriority.Required, "string");
            p.AddParameter(fileInputName);

            ITemplateParameter projectName;
            p.TryGetParameter("projectname", out projectName);

            p.ParameterValues[safeProjectName] = p.ParameterValues[projectName];
            p.ParameterValues[itemName] = p.ParameterValues[projectName];
            p.ParameterValues[safeItemName] = p.ParameterValues[projectName];
            p.ParameterValues[fileInputName] = p.ParameterValues[projectName];
        }

        private class Parameter : ITemplateParameter
        {
            public Parameter(string name, TemplateParameterPriority priority, string type, bool isName = false, string documentation = null, string defaultValue = null)
            {
                Name = name;
                Priority = priority;
                Type = type;
                IsName = isName;
                Documentation = documentation;
                DefaultValue = defaultValue;
            }

            public string Documentation { get; }

            public bool IsName { get; }

            public string DefaultValue { get; }

            public string Name { get; }

            public TemplateParameterPriority Priority { get; }

            public string Type { get; }
        }

        private class ParameterSet : IParameterSet
        {
            private readonly IDictionary<string, ITemplateParameter> _parameters = new Dictionary<string, ITemplateParameter>(StringComparer.OrdinalIgnoreCase);

            public ParameterSet()
            {
                AddParameter(new Parameter("projectname", TemplateParameterPriority.Implicit, "string", true));
                AddParameter(new Parameter("rootnamespace", TemplateParameterPriority.Optional, "string", documentation: "The root namespace of the current project. This parameter applies only to item templates."));
            }

            public IEnumerable<ITemplateParameter> Parameters => _parameters.Values;

            public IDictionary<ITemplateParameter, string> ParameterValues { get; } = new Dictionary<ITemplateParameter, string>();

            public IEnumerable<string> RequiredBrokerCapabilities => Enumerable.Empty<string>();

            public void AddParameter(ITemplateParameter param)
            {
                _parameters[param.Name] = param;
            }

            public bool TryGetParameter(string name, out ITemplateParameter parameter)
            {
                if (_parameters.TryGetValue(name, out parameter))
                {
                    return true;
                }

                parameter = new Parameter(name, TemplateParameterPriority.Optional, "string");
                return true;
            }
        }
    }
}
