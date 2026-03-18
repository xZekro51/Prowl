Shader "Default/SelectionOutline"

Properties
{
    _MainTex ("Texture", Texture2D) = "white"
    _SilhouetteTex ("Silhouette", Texture2D) = "white"
    _OutlineColor ("Outline Color", Color) = (0.28, 0.56, 1.0, 0.9)
    _OutlineWidth ("Outline Width", Float) = 2.0
}

// Pass 0: Silhouette — render selected objects as flat white
Pass "Silhouette"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    Cull Back
    ZTest Off
    ZWrite Off

	GLSLPROGRAM

	Vertex
	{
        #include "ShaderVariables"
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
			finalColor = vec4(1.0, 1.0, 1.0, 1.0);
		}
	}

	ENDGLSL
}

// Pass 1: Separable Gaussian blur (used twice: horizontal then vertical)
Pass "Blur"
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
		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		uniform sampler2D _MainTex;
		uniform vec2 _Direction; // (1/w, 0) for horizontal or (0, 1/h) for vertical
		uniform float _OutlineWidth;

		void main()
		{
			// 9-tap Gaussian kernel with configurable step size
			float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);

			vec2 step = _Direction * _OutlineWidth;

			float result = texture(_MainTex, TexCoords).r * weights[0];

			for (int i = 1; i < 5; i++)
			{
				vec2 offset = step * float(i);
				result += texture(_MainTex, TexCoords + offset).r * weights[i];
				result += texture(_MainTex, TexCoords - offset).r * weights[i];
			}

			finalColor = vec4(result, result, result, 1.0);
		}
	}

	ENDGLSL
}

// Pass 2: Composite — blend outline over the scene
Pass "Composite"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Alpha
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
		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		uniform sampler2D _MainTex;        // blurred silhouette
		uniform sampler2D _SilhouetteTex;  // original silhouette (mask)
		uniform vec4 _OutlineColor;

		void main()
		{
			float blurred = texture(_MainTex, TexCoords).r;
			float mask = texture(_SilhouetteTex, TexCoords).r;

			// Outline = blurred region minus the solid interior
			float outline = clamp(blurred - mask, 0.0, 1.0);

			// Smooth the fade for a softer edge
			outline = smoothstep(0.0, 1.0, outline);

			finalColor = vec4(_OutlineColor.rgb, outline * _OutlineColor.a);
		}
	}

	ENDGLSL
}
