Shader "Default/PointLight"

Properties
{
}

Pass "PointLight"
{
    Tags { "RenderOrder" = "Opaque" }

    // Sphere volume settings
    Cull Front  // Cull front faces so we only render back faces (inside of sphere)
    ZTest Greater  // Only render where sphere is in front of geometry
    ZWrite Off
    Blend Additive  // Additive blending for light accumulation

	GLSLPROGRAM

	Vertex
	{
		#include "Fragment"

		layout (location = 0) in vec3 vertexPosition;

		void main()
		{
			// Transform sphere vertex using model-view-projection matrices
			vec4 worldPos = prowl_ObjectToWorld * vec4(vertexPosition, 1.0);
			gl_Position = PROWL_MATRIX_VP * worldPos;
		}
	}

	Fragment
	{
		#include "Fragment"
		#include "PBR"
		#include "Shadow"

		layout (location = 0) out vec4 finalColor;

		// GBuffer textures
		uniform sampler2D _GBufferA; // RGB = Albedo, A = AO
		uniform sampler2D _GBufferB; // RGB = Normal (view space), A = ShadingMode
		uniform sampler2D _GBufferC; // R = Roughness, G = Metalness, B = Specular, A = Unused
		uniform sampler2D _GBufferD; // Custom Data per Shading Mode (e.g., Emissive for Lit mode)
		uniform sampler2D _CameraDepthTexture; // Depth texture
		uniform sampler2D _ShadowAtlas;

		// Point light uniforms
		uniform vec3 _LightPosition;
		uniform vec4 _LightColor;
		uniform float _LightIntensity;
		uniform float _LightRange;
		uniform float _ShadowsEnabled;
		uniform float _ShadowBias;
		uniform float _ShadowNormalBias;
		uniform float _ShadowStrength;
		uniform float _ShadowQuality;

		// Shadow matrices and face parameters (6 cube faces)
		uniform mat4 _ShadowMatrix0;
		uniform mat4 _ShadowMatrix1;
		uniform mat4 _ShadowMatrix2;
		uniform mat4 _ShadowMatrix3;
		uniform mat4 _ShadowMatrix4;
		uniform mat4 _ShadowMatrix5;
		uniform vec4 _ShadowFaceParams0; // xy: atlasPos, z: faceSize, w: farPlane
		uniform vec4 _ShadowFaceParams1;
		uniform vec4 _ShadowFaceParams2;
		uniform vec4 _ShadowFaceParams3;
		uniform vec4 _ShadowFaceParams4;
		uniform vec4 _ShadowFaceParams5;

		// Sample point light shadow from cubemap (6 faces in 3x2 grid)
		float SampleShadow(vec3 worldPos, vec3 worldNormal)
		{
			if (_ShadowsEnabled < 0.5) {
				return 0.0; // No shadows
			}

			// Calculate direction from light to fragment
			vec3 lightToFrag = worldPos - _LightPosition;
			vec3 absDir = abs(lightToFrag);

			// Determine dominant axis to select cube face
			// Face layout: [0:+X][1:-X][2:+Y]
			//              [3:-Y][4:+Z][5:-Z]
			int faceIndex = 0;
			mat4 shadowMatrix;
			vec4 faceParams;

			if (absDir.x >= absDir.y && absDir.x >= absDir.z) {
				// X is dominant
				if (lightToFrag.x > 0.0) {
					faceIndex = 0; // +X
					shadowMatrix = _ShadowMatrix0;
					faceParams = _ShadowFaceParams0;
				} else {
					faceIndex = 1; // -X
					shadowMatrix = _ShadowMatrix1;
					faceParams = _ShadowFaceParams1;
				}
			} else if (absDir.y >= absDir.x && absDir.y >= absDir.z) {
				// Y is dominant
				if (lightToFrag.y > 0.0) {
					faceIndex = 2; // +Y
					shadowMatrix = _ShadowMatrix2;
					faceParams = _ShadowFaceParams2;
				} else {
					faceIndex = 3; // -Y
					shadowMatrix = _ShadowMatrix3;
					faceParams = _ShadowFaceParams3;
				}
			} else {
				// Z is dominant
				if (lightToFrag.z > 0.0) {
					faceIndex = 4; // +Z
					shadowMatrix = _ShadowMatrix4;
					faceParams = _ShadowFaceParams4;
				} else {
					faceIndex = 5; // -Z
					shadowMatrix = _ShadowMatrix5;
					faceParams = _ShadowFaceParams5;
				}
			}

			// Apply normal bias
			vec3 worldPosBiased = worldPos + (normalize(worldNormal) * _ShadowNormalBias);

			// Transform to shadow space
			vec4 lightSpacePos = shadowMatrix * vec4(worldPosBiased, 1.0);
			vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
			projCoords = projCoords * 0.5 + 0.5;

			// Early exit if outside shadow map
			if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0) {
				return 0.0;
			}

			// Get shadow atlas coordinates using common helper
			vec2 shadowAtlasSize = vec2(textureSize(_ShadowAtlas, 0));
			float atlasSize = shadowAtlasSize.x;
			vec2 atlasCoords, shadowMin, shadowMax;
			GetAtlasCoordinates(projCoords, faceParams, atlasSize, atlasCoords, shadowMin, shadowMax);

			// Calculate bias for point light
			float finalBias = CalculateSlopeBias(worldNormal, normalize(lightToFrag), _ShadowBias);
			float currentDepth = projCoords.z - finalBias;

			// Sample shadow using common PCF helper
			return SampleShadowPCF(_ShadowAtlas, atlasCoords, shadowMin, shadowMax,
			                       currentDepth, _ShadowQuality, _ShadowStrength);
		}

		// Calculate point light contribution
		vec3 CalculatePointLight(vec3 worldPos, vec3 worldNormal, vec3 cameraPos, vec3 albedo, float metallic, float roughness, float ao)
		{
			vec3 lightToPixel = worldPos - _LightPosition;
			float distance = length(lightToPixel);
			vec3 lightDir = normalize(-lightToPixel);
			vec3 viewDir = normalize(-(worldPos - cameraPos));
			vec3 halfDir = normalize(lightDir + viewDir);

			// Distance attenuation (inverse square law with smooth falloff at range)
			float distanceAttenuation = 1.0 / (distance * distance + 1.0);
			float rangeAttenuation = 1.0 - smoothstep(_LightRange * 0.8, _LightRange, distance);
			float attenuation = distanceAttenuation * rangeAttenuation;

			// Early exit if outside range
			if (attenuation <= 0.0001) {
				return vec3(0.0);
			}

			// Calculate base reflectivity for PBR
			vec3 F0 = vec3(0.04);
			F0 = mix(F0, albedo, metallic);

			// Light radiance with attenuation
			vec3 radiance = _LightColor.rgb * _LightIntensity * attenuation;

			// Cook-Torrance BRDF
			float NDF = DistributionGGX(worldNormal, halfDir, roughness);
			float G = GeometrySmith(worldNormal, viewDir, lightDir, roughness);
			vec3 F = FresnelSchlick(max(dot(halfDir, viewDir), 0.0), F0);

			// Specular and diffuse
			vec3 kS = F;
			vec3 kD = vec3(1.0) - kS;
			kD *= 1.0 - metallic;

			float NdotL = max(dot(worldNormal, lightDir), 0.0);

			// Specular term
			vec3 numerator = NDF * G * F;
			float denominator = 4.0 * max(dot(worldNormal, viewDir), 0.0) * NdotL + 0.0001;
			vec3 specular = numerator / denominator;

			// Calculate shadow
			float shadow = SampleShadow(worldPos, worldNormal);
			float shadowFactor = 1.0 - shadow;

			// Final lighting
			vec3 diffuse = kD * albedo / PI;
			return (diffuse + specular) * radiance * NdotL * shadowFactor * ao;
		}

		void main()
		{
			// Calculate screen-space texture coordinates from fragment position
			vec2 TexCoords = gl_FragCoord.xy / _ScreenParams.xy;

			// Sample GBuffer
			vec4 gbufferA = texture(_GBufferA, TexCoords);
			vec4 gbufferB = texture(_GBufferB, TexCoords);
			vec4 gbufferC = texture(_GBufferC, TexCoords);

			// Extract material properties
			vec3 albedo = gbufferA.rgb;
			float ao = gbufferA.a;

			// Decode normal from [0,1] to [-1,1] range
			vec3 viewNormal = gbufferB.rgb * 2.0 - 1.0;
			float shadingMode = gbufferB.a;

			float roughness = gbufferC.r;
			float metallic = gbufferC.g;

			// Sample depth
			float depth = texture(_CameraDepthTexture, TexCoords).r;

			// Reconstruct world position
			vec3 worldPos = WorldPosFromDepth(depth, TexCoords);

			// Transform normal from view space to world space
			vec3 worldNormal = normalize(mat3(PROWL_MATRIX_I_V) * viewNormal);

			// Check shading mode (0 = Unlit, 1 = Lit)
			// Use threshold comparison to handle floating point precision
			if (shadingMode < 0.5) {
				finalColor = vec4(0.0, 0.0, 0.0, 0.0);
				return;
			}

			// Calculate point light contribution
			vec3 lighting = CalculatePointLight(worldPos, worldNormal, _WorldSpaceCameraPos.xyz, albedo, metallic, roughness, ao);

			finalColor = vec4(lighting, 1.0);
		}
	}

	ENDGLSL
}
