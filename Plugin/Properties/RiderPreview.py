# Rhino's development package scan may know the plug-in ID before it knows its commands.
# Load the compiled assembly before dispatching the preview on a fresh Rider launch.
import Rhino
import System

plugin_id = System.Guid("09D603C7-8651-4969-B62B-C8EA8CCDBE54")
if not Rhino.PlugIns.PlugIn.LoadPlugIn(plugin_id):
    raise RuntimeError("Rhino could not load the ThreeDeePrint build.")
Rhino.RhinoApp.RunScript("_FlatPackPreview", True)
