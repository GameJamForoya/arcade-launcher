Shader "ArcadeLauncher/CRT"
{
    Properties
    {
        _Distortion          ("Barrel Distortion",       Range(0.0, 0.5))   = 0.12
        _ScanlineIntensity   ("Scanline Intensity",      Range(0.0, 1.0))   = 0.30
        _ScanlineCount       ("Scanline Count",          Range(50.0, 800.0))= 240.0
        _Vignette            ("Vignette",                Range(0.0, 1.0))   = 0.55
        _VignetteSoftness    ("Vignette Softness",       Range(0.0, 1.0))   = 0.45
        _ChromaticAberration ("Chromatic Aberration",    Range(0.0, 0.02))  = 0.0025
        _CornerRadius        ("Bezel Corner Radius",     Range(0.0, 0.2))   = 0.04
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "CRT"

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Distortion;
            float _ScanlineIntensity;
            float _ScanlineCount;
            float _Vignette;
            float _VignetteSoftness;
            float _ChromaticAberration;
            float _CornerRadius;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;

                // 1. Barrel distortion — pull UVs outward
                float2 centered = uv - 0.5;
                float  r2       = dot(centered, centered);
                float2 bentUV   = centered * (1.0 + _Distortion * r2) + 0.5;

                // 2. Black border for the rounded bezel
                float2 bezelDist = max(abs(centered) - (0.5 - _CornerRadius), 0.0);
                float  outsideBezel = step(_CornerRadius, length(bezelDist));
                float  outsideUV    = step(1.0, max(saturate(bentUV.x) == bentUV.x ? 0.0 : 1.0,
                                                    saturate(bentUV.y) == bentUV.y ? 0.0 : 1.0));
                if (outsideUV > 0.5 || outsideBezel > 0.5) return half4(0.0, 0.0, 0.0, 1.0);

                // 3. Sample scene with optional chromatic aberration
                half3 rgb;
                if (_ChromaticAberration > 0.0)
                {
                    float2 caOffset = centered * _ChromaticAberration;
                    rgb.r = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, bentUV + caOffset).r;
                    rgb.g = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, bentUV).g;
                    rgb.b = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, bentUV - caOffset).b;
                }
                else
                {
                    rgb = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, bentUV).rgb;
                }

                // 4. Scanlines — horizontal bands modulate brightness
                float scanline = 0.5 + 0.5 * sin(bentUV.y * _ScanlineCount * 6.28318530718);
                rgb *= 1.0 - _ScanlineIntensity * scanline;

                // 5. Vignette — darken corners based on radial distance
                float vMin   = 0.5 - _VignetteSoftness * 0.5;
                float vMax   = 0.85;
                float vMask  = smoothstep(vMin, vMax, length(centered));
                rgb *= 1.0 - _Vignette * vMask;

                return half4(rgb, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
