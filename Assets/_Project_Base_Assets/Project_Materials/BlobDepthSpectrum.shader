Shader "Custom/BlobDepthSpectrum"
{
    Properties
    {
        [Header(Depth Spectrum)]
        _HeightLUT ("Height LUT", 2D) = "white" {}
    }

    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "UnlitPass"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR; // Receives vertex color from C#
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
            };

            TEXTURE2D(_HeightLUT);
            SAMPLER(sampler_HeightLUT);

            Varyings vert(Attributes input)
            {
                Varyings output;
                
                // URP space transformation
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                
                output.color = input.color; // Pass normalized depth down to fragment
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // input.color.r holds normalized depth t in range [0, 1]
                float t = saturate(input.color.r);

                half4 finalColor = SAMPLE_TEXTURE2D(_HeightLUT, sampler_HeightLUT, float2(t, 0.5));

                return finalColor;
            }
            ENDHLSL
        }
    }
}