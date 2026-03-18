Shader "Default/DebugView"

Properties
{
    _MainTex ("Texture", Texture2D) = "white"
}

Pass "DepthVisualize"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    Cull None
    ZTest Off
    ZWrite Off

	GLSLPROGRAM

	Vertex
	{
		layout (location = 0) in vec3 vertexPosition;
		layout (location = 1) in vec2 vertexTexCoord;

		out vec2 TexCoords;

		void main()
		{
			TexCoords = vertexTexCoord;
		    gl_Position = vec4(vertexPosition, 1.0);
		}
	}

	Fragment
	{
        #include "Fragment"

		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		uniform sampler2D _MainTex;

		void main()
		{
			float depth = texture(_MainTex, TexCoords).r;
			float linearDepth = linearizeDepthFromProjection(depth);
			float near = _ProjectionParams.y;
			float far  = _ProjectionParams.z;
			float normalized = clamp(linearDepth / far, 0.0, 1.0);
			// Invert so near is white, far is black
			float vis = 1.0 - normalized;
			finalColor = vec4(vis, vis, vis, 1.0);
		}
	}

	ENDGLSL
}

Pass "Overdraw"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Additive
    Cull None
    ZTest Off
    ZWrite Off

	GLSLPROGRAM

	Vertex
	{
        #include "Fragment"
        #include "VertexAttributes"

		void main()
		{
			gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
		}
	}

	Fragment
	{
		layout (location = 0) out vec4 finalColor;

		void main()
		{
			finalColor = vec4(0.1, 0.1, 0.1, 1.0);
		}
	}

	ENDGLSL
}
