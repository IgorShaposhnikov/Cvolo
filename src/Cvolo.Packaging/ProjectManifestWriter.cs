using System.Xml.Linq;

namespace Cvolo.Packaging;

public static class ProjectManifestWriter
{
	public static void AddPackageReference(ProjectManifest manifest, string id, string range)
	{
		var doc = XDocument.Load(manifest.ProjectPath);
		var root = doc.Root ?? throw new InvalidDataException("Project file has no root element.");
		var existing = root.Elements("ItemGroup").Elements("PackageReference")
			.FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase));
		if (existing is not null)
		{
			existing.SetAttributeValue("Version", range);
			Save(doc, manifest.ProjectPath);
			return;
		}

		var itemGroup = root.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageReference").Any());
		if (itemGroup is null)
		{
			itemGroup = new XElement("ItemGroup");
			root.Add(itemGroup);
		}

		itemGroup.Add(new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", range)));
		Save(doc, manifest.ProjectPath);
	}

	public static bool RemovePackageReference(ProjectManifest manifest, string id)
	{
		var doc = XDocument.Load(manifest.ProjectPath);
		var references = doc.Root?.Elements("ItemGroup").Elements("PackageReference")
			.Where(e => string.Equals((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase))
			.ToList() ?? [];
		foreach (var reference in references)
			reference.Remove();
		if (references.Count > 0)
			Save(doc, manifest.ProjectPath);
		return references.Count > 0;
	}

	private static void Save(XDocument doc, string path) => doc.Save(path);
}
