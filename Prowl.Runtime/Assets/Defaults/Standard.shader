Shader "Default/Standard"

Properties
{
	_RenderMode ("Render Mode (0=Opaque, 1=Cutout, 2=Transparent)", Float) = 0.0
	_AlphaCutoff ("Alpha Cutoff", Float) = 0.5

	_MainTex ("Albedo", Texture2D) = "grid"
	_MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
	_UVTiling ("UV Tiling", Vector2) = (1.0, 1.0)
	_UVOffset ("UV Offset", Vector2) = (0.0, 0.0)

	_NormalTex ("Normal Map", Texture2D) = "normal"
	_NormalStrength ("Normal Strength", Float) = 1.0

	_SurfaceTex ("Surface (AO, Roughness, Metallic)", Texture2D) = "surface"
	_Metallic ("Metallic", Float) = 0.0
	_Roughness ("Roughness", Float) = 0.5
	_AOStrength ("AO Strength", Float) = 1.0
	_Specular ("Specular", Float) = 0.5

	_EmissionTex ("Emission", Texture2D) = "emission"
	_EmissionColor ("Emission Color", Color) = (0.0, 0.0, 0.0, 1.0)
	_EmissionIntensity ("Emission Intensity", Float) = 1.0

	_HeightTex ("Height Map", Texture2D) = "black"
	_HeightScale ("Height Scale", Float) = 0.05

}

Pass "Standard"
{
	Tags { "RenderOrder" = "Opaque" }

	Cull Back

	GLSLPROGRAM

		Vertex
		{
			#include "Fragment"
			#include "VertexAttributes"

			out vec2 texCoord0;
			out vec3 worldPos;
			out vec4 vColor;
			out vec3 vNormal;
			out vec3 vTangent;
			out vec3 vBitangent;

			void main()
			{
#ifdef SKINNED
				vec4 skinnedPos = GetSkinnedPosition(vertexPosition);
				vec3 skinnedNormal = GetSkinnedNormal(vertexNormal);

				gl_Position = PROWL_MATRIX_MVP * skinnedPos;
				texCoord0 = vertexTexCoord0;
				worldPos = (PROWL_MATRIX_M * skinnedPos).xyz;
				vColor = vertexColor;
				vNormal = normalize(mat3(PROWL_MATRIX_M) * skinnedNormal);
#ifdef HAS_TANGENTS
				vec3 skinnedTangent = GetSkinnedNormal(vertexTangent.xyz);
				vTangent = normalize(mat3(PROWL_MATRIX_M) * skinnedTangent);
				vBitangent = cross(vNormal, vTangent);
#endif
#else
				gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
				texCoord0 = vertexTexCoord0;
				worldPos = (PROWL_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
				vColor = vertexColor;
				vNormal = normalize(mat3(PROWL_MATRIX_M) * vertexNormal);
#ifdef HAS_TANGENTS
				vTangent = normalize(mat3(PROWL_MATRIX_M) * vertexTangent.xyz);
				vBitangent = cross(vNormal, vTangent);
#endif
#endif
			}
		}

		Fragment
		{
			#include "Fragment"

			// GBuffer layout:
			// BufferA: RGB = Albedo, A = AO
			// BufferB: RGB = Normal (view space), A = ShadingMode
			// BufferC: R = Roughness, G = Metalness, B = Specular, A = Unused
			// BufferD: Custom Data per Shading Mode
			layout (location = 0) out vec4 gBufferA;
			layout (location = 1) out vec4 gBufferB;
			layout (location = 2) out vec4 gBufferC;
			layout (location = 3) out vec4 gBufferD;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 vColor;
			in vec3 vNormal;
			in vec3 vTangent;
			in vec3 vBitangent;

			uniform sampler2D _MainTex;
			uniform sampler2D _NormalTex;
			uniform sampler2D _SurfaceTex;
			uniform sampler2D _EmissionTex;
			uniform sampler2D _HeightTex;

			uniform vec4 _MainColor;
			uniform vec2 _UVTiling;
			uniform vec2 _UVOffset;
			uniform float _NormalStrength;
			uniform float _Metallic;
			uniform float _Roughness;
			uniform float _AOStrength;
			uniform float _Specular;
			uniform vec4 _EmissionColor;
			uniform float _EmissionIntensity;
			uniform float _HeightScale;
			uniform float _RenderMode;
			uniform float _AlphaCutoff;

			vec2 ParallaxMapping(vec2 uv, vec3 viewDirTS)
			{
				float height = texture(_HeightTex, uv).r;
				vec2 offset = viewDirTS.xy / viewDirTS.z * (height * _HeightScale);
				return uv - offset;
			}

			void main()
			{
				// Transparent objects skip the GBuffer pass entirely
				if (_RenderMode > 1.5) discard;

				vec2 uv = texCoord0 * _UVTiling + _UVOffset;

#ifdef HAS_TANGENTS
				// Parallax mapping
				if (_HeightScale > 0.001)
				{
					mat3 TBN = mat3(normalize(vTangent), normalize(vBitangent), normalize(vNormal));
					vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
					vec3 viewDirTS = transpose(TBN) * viewDir;
					uv = ParallaxMapping(uv, viewDirTS);
				}
#endif

				// Albedo
				vec4 albedo = texture(_MainTex, uv) * vColor * _MainColor;

				// Alpha Cutout
				if (_RenderMode > 0.5 && albedo.a < _AlphaCutoff) discard;

				// Normals
				vec3 worldNormal;
#ifdef HAS_TANGENTS
				mat3 TBN = mat3(normalize(vTangent), normalize(vBitangent), normalize(vNormal));
				vec3 normalMapSample = texture(_NormalTex, uv).rgb;
				vec3 normalTS = normalMapSample * 2.0 - 1.0;
				normalTS.xy *= _NormalStrength;
				normalTS = normalize(normalTS);
				worldNormal = normalize(TBN * normalTS);
#else
				worldNormal = normalize(vNormal);
#endif
				vec3 viewNormal = normalize(mat3(PROWL_MATRIX_V) * worldNormal);

				// Surface properties
				vec4 surface = texture(_SurfaceTex, uv);
				float ao = mix(1.0, 1.0 - surface.r, _AOStrength);
				float roughness = surface.g * _Roughness;
				float metallic = surface.b + _Metallic;
				metallic = clamp(metallic, 0.0, 1.0);

				// Emission
				vec3 emission = texture(_EmissionTex, uv).rgb * _EmissionColor.rgb * _EmissionIntensity;

				// Convert albedo to linear space
				vec3 baseColor = gammaToLinearSpace(albedo.rgb);

				float specular = mix(0.04, 1.0, metallic) * _Specular * 2.0;

				// Output to GBuffer
				gBufferA = vec4(baseColor, ao);
				gBufferB = vec4(viewNormal * 0.5 + 0.5, 1.0);
				gBufferC = vec4(roughness, metallic, specular, 0.0);
				gBufferD = vec4(emission, 0.0);
			}
		}
	ENDGLSL
}

Pass "StandardTransparent"
{
	Tags { "RenderOrder" = "Transparent" }

	Blend Alpha
	ZWrite Off
	Cull Back

	GLSLPROGRAM

		Vertex
		{
			#include "Fragment"
			#include "VertexAttributes"

			out vec2 texCoord0;
			out vec3 worldPos;
			out vec4 vColor;
			out vec3 vNormal;
			out vec3 vTangent;
			out vec3 vBitangent;

			void main()
			{
#ifdef SKINNED
				vec4 skinnedPos = GetSkinnedPosition(vertexPosition);
				vec3 skinnedNormal = GetSkinnedNormal(vertexNormal);

				gl_Position = PROWL_MATRIX_MVP * skinnedPos;
				texCoord0 = vertexTexCoord0;
				worldPos = (PROWL_MATRIX_M * skinnedPos).xyz;
				vColor = vertexColor;
				vNormal = normalize(mat3(PROWL_MATRIX_M) * skinnedNormal);
#ifdef HAS_TANGENTS
				vec3 skinnedTangent = GetSkinnedNormal(vertexTangent.xyz);
				vTangent = normalize(mat3(PROWL_MATRIX_M) * skinnedTangent);
				vBitangent = cross(vNormal, vTangent);
#endif
#else
				gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
				texCoord0 = vertexTexCoord0;
				worldPos = (PROWL_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
				vColor = vertexColor;
				vNormal = normalize(mat3(PROWL_MATRIX_M) * vertexNormal);
#ifdef HAS_TANGENTS
				vTangent = normalize(mat3(PROWL_MATRIX_M) * vertexTangent.xyz);
				vBitangent = cross(vNormal, vTangent);
#endif
#endif
			}
		}

		Fragment
		{
			#include "Fragment"
			#include "PBR"

			layout (location = 0) out vec4 finalColor;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 vColor;
			in vec3 vNormal;
			in vec3 vTangent;
			in vec3 vBitangent;

			uniform sampler2D _MainTex;
			uniform sampler2D _NormalTex;
			uniform sampler2D _SurfaceTex;
			uniform sampler2D _EmissionTex;
			uniform sampler2D _HeightTex;

			uniform vec4 _MainColor;
			uniform vec2 _UVTiling;
			uniform vec2 _UVOffset;
			uniform float _NormalStrength;
			uniform float _Metallic;
			uniform float _Roughness;
			uniform float _AOStrength;
			uniform float _Specular;
			uniform vec4 _EmissionColor;
			uniform float _EmissionIntensity;
			uniform float _HeightScale;
			uniform float _RenderMode;

			// Forward lighting globals (set by the render pipeline)
			uniform vec3 _ForwardLightDir;
			uniform vec3 _ForwardLightColor;
			uniform vec3 _ForwardAmbientColor;
			uniform float _ForwardAmbientStrength;

			void main()
			{
				// Only transparent objects use this pass
				if (_RenderMode < 1.5) discard;

				vec2 uv = texCoord0 * _UVTiling + _UVOffset;

#ifdef HAS_TANGENTS
				if (_HeightScale > 0.001)
				{
					mat3 TBNp = mat3(normalize(vTangent), normalize(vBitangent), normalize(vNormal));
					vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
					vec3 viewDirTS = transpose(TBNp) * viewDir;
					float height = texture(_HeightTex, uv).r;
					uv -= viewDirTS.xy / viewDirTS.z * (height * _HeightScale);
				}
#endif

				// Albedo
				vec4 albedo = texture(_MainTex, uv) * vColor * _MainColor;

				// Normals
				vec3 worldNormal;
#ifdef HAS_TANGENTS
				mat3 TBN = mat3(normalize(vTangent), normalize(vBitangent), normalize(vNormal));
				vec3 normalMapSample = texture(_NormalTex, uv).rgb;
				vec3 normalTS = normalMapSample * 2.0 - 1.0;
				normalTS.xy *= _NormalStrength;
				normalTS = normalize(normalTS);
				worldNormal = normalize(TBN * normalTS);
#else
				worldNormal = normalize(vNormal);
#endif

				// Surface properties
				vec4 surface = texture(_SurfaceTex, uv);
				float ao = mix(1.0, 1.0 - surface.r, _AOStrength);
				float roughness = surface.g * _Roughness;
				float metallic = clamp(surface.b + _Metallic, 0.0, 1.0);

				// Emission
				vec3 emission = texture(_EmissionTex, uv).rgb * _EmissionColor.rgb * _EmissionIntensity;

				// Convert to linear space
				vec3 baseColor = gammaToLinearSpace(albedo.rgb);

				// Forward PBR lighting
				vec3 N = normalize(worldNormal);
				vec3 V = normalize(_WorldSpaceCameraPos.xyz - worldPos);
				vec3 L = normalize(-_ForwardLightDir);
				vec3 H = normalize(V + L);

				float NdotL = max(dot(N, L), 0.0);
				float NdotV = max(dot(N, V), 0.001);

				vec3 F0 = mix(vec3(0.04), baseColor, metallic);

				// Cook-Torrance specular BRDF
				float D = DistributionGGX(N, H, roughness);
				float G = GeometrySmith(N, V, L, roughness);
				vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

				vec3 numerator = D * G * F;
				float denominator = 4.0 * NdotV * NdotL + 0.0001;
				vec3 spec = numerator / denominator;

				vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
				vec3 directLighting = (kD * baseColor / PI + spec) * _ForwardLightColor * NdotL;

				// Ambient
				vec3 ambient = _ForwardAmbientColor * _ForwardAmbientStrength * baseColor * ao;

				vec3 color = ambient + directLighting + emission;
				finalColor = vec4(color, albedo.a);
			}
		}
	ENDGLSL
}

Pass "StandardShadow"
{
	Tags { "LightMode" = "ShadowCaster" }

	Cull Back

	GLSLPROGRAM

		Vertex
		{
			#include "Fragment"
			#include "VertexAttributes"

			out vec2 texCoord0;

			void main()
			{
#ifdef SKINNED
				vec4 skinnedPos = GetSkinnedPosition(vertexPosition);
				gl_Position = PROWL_MATRIX_MVP * skinnedPos;
#else
				gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
#endif
				texCoord0 = vertexTexCoord0;
			}
		}

		Fragment
		{
			#include "Fragment"

			in vec2 texCoord0;

			uniform sampler2D _MainTex;
			uniform vec4 _MainColor;
			uniform vec2 _UVTiling;
			uniform vec2 _UVOffset;
			uniform float _RenderMode;
			uniform float _AlphaCutoff;

			void main()
			{
				// Transparent objects do not cast shadows by default
				if (_RenderMode > 1.5) discard;

				// Alpha Cutout: test alpha in shadow pass too
				if (_RenderMode > 0.5)
				{
					vec2 uv = texCoord0 * _UVTiling + _UVOffset;
					float alpha = texture(_MainTex, uv).a * _MainColor.a;
					if (alpha < _AlphaCutoff) discard;
				}

				gl_FragDepth = gl_FragCoord.z;
			}
		}
	ENDGLSL
}
