Shader "Default/Terrain"

Properties
{
    _Heightmap ("Heightmap", Texture2D) = "black"
    _Splatmap ("Splatmap", Texture2D) = "white"
    _Layer0 ("Layer 0 Albedo", Texture2D) = "white"
    _Layer1 ("Layer 1 Albedo", Texture2D) = "white"
    _Layer2 ("Layer 2 Albedo", Texture2D) = "white"
    _Layer3 ("Layer 3 Albedo", Texture2D) = "white"
    _TerrainSize ("Terrain Size", Float) = 1024.0
    _TerrainHeight ("Terrain Height", Float) = 100.0
    _TextureTiling ("Texture Tiling", Float) = 10.0
}

Pass "Terrain"
{
    Tags { "RenderOrder" = "Opaque" }

    Cull Back
    ZWrite On
    Blend Off

	GLSLPROGRAM

		Vertex
		{
            #include "Fragment"
            #include "VertexAttributes"

			out vec2 texCoord0;
            out vec3 worldPos;
            out vec3 worldNormal;

#ifdef GPU_INSTANCING
            // Instance attributes (semantic 8-13)
            layout(location = 8) in vec4 instanceModelRow0;
            layout(location = 9) in vec4 instanceModelRow1;
            layout(location = 10) in vec4 instanceModelRow2;
            layout(location = 11) in vec4 instanceModelRow3;
            layout(location = 12) in vec4 instanceColor;
            layout(location = 13) in vec4 instanceCustomData;
#endif

            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;

            uniform vec3 _TerrainOffset;

			void main()
			{
#ifdef GPU_INSTANCING
                // Construct instance matrix
                mat4 instanceModel = mat4(
                    instanceModelRow0,
                    instanceModelRow1,
                    instanceModelRow2,
                    instanceModelRow3
                );

                // Extract chunk position and scale
                vec3 chunkPosition = instanceModelRow3.xyz;
                float chunkScale = length(instanceModelRow0.xyz);

                // Vertex position is in 0-1 range, scale to chunk size
                vec3 localPos = vertexPosition * chunkScale;
                vec3 worldPosition = chunkPosition + localPos;

                // Calculate UV for heightmap sampling (in 0-1 terrain space)
                vec2 terrainUV = (worldPosition.xz - _TerrainOffset.xz) / _TerrainSize;
                texCoord0 = terrainUV;

                // Sample heightmap for vertex displacement
                float height = texture(_Heightmap, terrainUV).r;
                worldPosition.y = worldPosition.y + (height * _TerrainHeight);

                // Calculate normal by sampling neighboring heights
                float heightmapSize = float(textureSize(_Heightmap, 0).x);
                float texelSize = heightmapSize > 0.0 ? (1.0 / heightmapSize) : 0.001;

                // Sample heights at neighboring texels
                float heightRight = texture(_Heightmap, terrainUV + vec2(texelSize, 0.0)).r * _TerrainHeight;
                float heightLeft = texture(_Heightmap, terrainUV - vec2(texelSize, 0.0)).r * _TerrainHeight;
                float heightUp = texture(_Heightmap, terrainUV + vec2(0.0, texelSize)).r * _TerrainHeight;
                float heightDown = texture(_Heightmap, terrainUV - vec2(0.0, texelSize)).r * _TerrainHeight;

                // Calculate world space step distance
                float worldStep = texelSize * _TerrainSize;

                // Calculate slopes using central differences
                float slopeX = (heightRight - heightLeft) / (worldStep * 2.0);
                float slopeZ = (heightUp - heightDown) / (worldStep * 2.0);

                // Normal from slopes: vec3(-dx, 1, -dz) then normalize
                worldNormal = normalize(vec3(-slopeX, 1.0, -slopeZ));

                worldPos = worldPosition;
                gl_Position = PROWL_MATRIX_VP * vec4(worldPosition, 1.0);
#else
                // Non-instanced fallback
                gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
                texCoord0 = vertexTexCoord0;
                worldPos = (PROWL_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
                worldNormal = normalize((PROWL_MATRIX_M * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
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
            in vec3 worldNormal;

			uniform sampler2D _Splatmap;
            uniform sampler2D _Layer0;
            uniform sampler2D _Layer1;
            uniform sampler2D _Layer2;
            uniform sampler2D _Layer3;
            uniform float _TextureTiling;

			void main()
			{
                // Sample splatmap for blend weights (RGBA = 4 layers)
                vec4 splatWeights = texture(_Splatmap, texCoord0);

                // Normalize weights to ensure they sum to 1
                float weightSum = splatWeights.r + splatWeights.g + splatWeights.b + splatWeights.a;
                if (weightSum > 0.0)
                    splatWeights /= weightSum;

                // Sample terrain textures with tiling
                vec2 tiledUV = texCoord0 * _TextureTiling;
                vec3 layer0 = texture(_Layer0, tiledUV).rgb;
                vec3 layer1 = texture(_Layer1, tiledUV).rgb;
                vec3 layer2 = texture(_Layer2, tiledUV).rgb;
                vec3 layer3 = texture(_Layer3, tiledUV).rgb;

                // Blend textures based on splatmap
                vec3 albedo = layer0 * splatWeights.r
                            + layer1 * splatWeights.g
                            + layer2 * splatWeights.b
                            + layer3 * splatWeights.a;

                // Convert to linear space
                vec3 baseColor = gammaToLinearSpace(albedo);

                // Calculate view-space normal
                vec3 viewNormal = normalize((PROWL_MATRIX_V * vec4(worldNormal, 0.0)).xyz);

				// Output to GBuffer
				// BufferA: RGB = Albedo, A = AO
				gBufferA = vec4(baseColor, 1.0);

				// BufferB: RGB = Normal (view space), A = ShadingMode
				// ShadingMode: 1 = Lit
				float shadingMode = 1.0;
				gBufferB = vec4(viewNormal * 0.5 + 0.5, shadingMode);

				// BufferC: R = Roughness, G = Metalness, B = Specular
				gBufferC = vec4(1.0, 0.0, 0.0, 0.0); // Rough, non-metallic

				// BufferD: Unused for standard lit
				gBufferD = vec4(0.0);
			}
		}
	ENDGLSL
}

Pass "TerrainShadow"
{
    Tags { "LightMode" = "ShadowCaster" }

    Cull Back

	GLSLPROGRAM

		Vertex
		{
            #include "Fragment"
            #include "VertexAttributes"

			out vec3 worldPos;

#ifdef GPU_INSTANCING
            // Instance attributes (semantic 8-13)
            layout(location = 8) in vec4 instanceModelRow0;
            layout(location = 9) in vec4 instanceModelRow1;
            layout(location = 10) in vec4 instanceModelRow2;
            layout(location = 11) in vec4 instanceModelRow3;
            layout(location = 12) in vec4 instanceColor;
#endif

            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;

            uniform vec3 _TerrainOffset;

			void main()
			{
#ifdef GPU_INSTANCING
                // Extract chunk position and scale
                vec3 chunkPosition = instanceModelRow3.xyz;
                float chunkScale = length(instanceModelRow0.xyz);

                // Vertex position is in 0-1 range, scale to chunk size
                vec3 localPos = vertexPosition * chunkScale;
                vec3 worldPosition = chunkPosition + localPos;

                // Calculate UV for heightmap sampling (in 0-1 terrain space)
                vec2 terrainUV = (worldPosition.xz - _TerrainOffset.xz) / _TerrainSize;

                // Sample heightmap for vertex displacement
                float height = texture(_Heightmap, terrainUV).r;
                worldPosition.y = worldPosition.y + (height * _TerrainHeight);

                worldPos = worldPosition;
                gl_Position = PROWL_MATRIX_VP * vec4(worldPosition, 1.0);
#else
                // Non-instanced fallback
                gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
                worldPos = (PROWL_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
#endif
			}
		}

		Fragment
		{
			#include "Fragment"

			in vec3 worldPos;

			void main()
			{
				gl_FragDepth = gl_FragCoord.z;
			}
		}
	ENDGLSL
}
