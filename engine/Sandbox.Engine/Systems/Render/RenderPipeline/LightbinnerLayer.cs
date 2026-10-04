namespace Sandbox.Rendering;

internal class LightbinnerLayer : RenderLayer
{
	public LightbinnerLayer()
	{
		Name = "Lightbinner";

		Flags |= LayerFlags.NeverRemove;
		Flags |= LayerFlags.LightBinnerSetupLayer;

		ObjectFlagsRequired = SceneObjectFlags.IsLight;
	}

	/// <summary>
	/// Configures the lightbinner to react to mat_fullbright and more
	/// </summary>
	/// <param name="pipelineAttributes"></param>
	public void Setup( NativeEngine.CRenderAttributes pipelineAttributes )
	{
		ObjectFlagsExcluded = SceneObjectFlags.None;
		if ( !pipelineAttributes.IsValid ) return;

		bool directLighting = pipelineAttributes.GetBoolValue( "directLighting", true );
		bool indirectLighting = pipelineAttributes.GetBoolValue( "indirectLighting", true );
		bool environmentMaps = pipelineAttributes.GetBoolValue( "environmentMaps", true );
		bool lightProbeVolumes = pipelineAttributes.GetBoolValue( "lightProbeVolumes", true );
		bool renderSun = pipelineAttributes.GetBoolValue( "renderSun", true );

		if ( !directLighting )
		{
			ObjectFlagsExcluded |= SceneObjectFlags.IsDirectLight;
		}

		if ( !indirectLighting )
		{
			ObjectFlagsExcluded |= SceneObjectFlags.IsIndirectLight;
		}

		if ( !renderSun )
		{
			ObjectFlagsExcluded |= SceneObjectFlags.IsSunLight;
		}

		if ( !environmentMaps )
		{
			ObjectFlagsExcluded |= SceneObjectFlags.IsEnvMap;
		}

		if ( !lightProbeVolumes )
		{
			ObjectFlagsExcluded |= SceneObjectFlags.IsLightVolume;
		}
	}
}
