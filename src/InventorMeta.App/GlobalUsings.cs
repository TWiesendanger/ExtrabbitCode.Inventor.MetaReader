// The app's source is organized into feature folders, each with its own namespace. Every type
// name across them is unique, so importing all of them globally lets any file reference any other
// without a per-file using - the same reach the old single flat namespace gave, now with the code
// grouped by feature. Folder-and-namespace collisions (e.g. the Analytics namespace holding an
// Analytics class) resolve to the type at every call site.
global using ExtrabbitCode.Inventor.MetaReader.App;
global using ExtrabbitCode.Inventor.MetaReader.App.Common;
global using ExtrabbitCode.Inventor.MetaReader.App.DevTools;
global using ExtrabbitCode.Inventor.MetaReader.App.Dialogs;
global using ExtrabbitCode.Inventor.MetaReader.App.Document;
global using ExtrabbitCode.Inventor.MetaReader.App.Onboarding;
global using ExtrabbitCode.Inventor.MetaReader.App.Samples;
global using ExtrabbitCode.Inventor.MetaReader.App.Settings;
global using ExtrabbitCode.Inventor.MetaReader.App.Shell;
global using ExtrabbitCode.Inventor.MetaReader.App.Telemetry;
global using ExtrabbitCode.Inventor.MetaReader.App.Viewer3D;
