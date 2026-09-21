Shader "UI/CompositeDepthViewer"
{
    Properties
    {
        _MainTex ("Source Texture (R16/RGBA)", 2D) = "white" {}
        _GameTex ("Beamer Game Scene", 2D) = "black" {}

        [Header(Elevation Bands Low To High)]
        _Color0 ("Elevation 0 (Lowest)", Color)  = (0.05, 0.05, 0.35, 1.0)
        _Color1 ("Elevation 1", Color)          = (0.10, 0.45, 0.75, 1.0)
        _Color2 ("Elevation 2", Color)          = (0.20, 0.55, 0.20, 1.0)
        _Color3 ("Elevation 3", Color)          = (0.80, 0.75, 0.25, 1.0)
        _Color4 ("Elevation 4", Color)          = (0.45, 0.28, 0.12, 1.0)
        _Color5 ("Elevation 5 (Highest)", Color)= (0.95, 0.95, 0.95, 1.0)

        [Header(Filtering)]
        _DepthMin ("Depth Min (mm)", Float) = 500.0
        _DepthMax ("Depth Max (mm)", Float) = 2000.0
        _Bounds ("Playground Bounds (xMin, yMin, xMax, yMax)", Vector) = (0, 0, 512, 424)

        [Header(Mode Controls)]
        _IsDepthMode ("Is Depth Mode", Float) = 1.0
        _IsColorMode ("Is Color Mode", Float) = 0.0
        _PostProcessEnabled ("Post Process Enabled", Float) = 1.0
        _FlipX ("Flip X", Float) = 0.0
        _FlipY ("Flip Y", Float) = 0.0
        _ShowGameScene ("Show Beamer Game Scene", Float) = 0.0
        _GameSceneAlpha ("Game Scene Transparency", Range(0,1)) = 1.0
        _UseBounds ("Use Bounds Mask", Float) = 0.0
    }

    SubShader
    {
        Tags 
        { 
            "Queue"="Transparent" 
            "IgnoreProjector"="True" 
            "RenderType"="Transparent" 
            "PreviewType"="Plane"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata {
                float4 vertex : POSITION;
                float4 color   : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                fixed4 color   : COLOR;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            sampler2D _OverlayTex;
            sampler2D _GameTex;
            float4 _MainTex_TexelSize; // z = width, w = height
            
            float _DepthMin;
            float _DepthMax;
            float4 _Bounds;
            float _UseBounds;

            float _IsDepthMode;
            float _IsColorMode;
            float _PostProcessEnabled;
            float _FlipX;
            float _FlipY;
            float _ShowGameScene;
            float _GameSceneAlpha;

            fixed4 _Color0, _Color1, _Color2, _Color3, _Color4, _Color5;

            v2f vert (appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 ElevationColor(float t)
            {
                t = saturate(t);
                float scaled = t * 5.0;
                int idx = (int)floor(scaled);
                float frac = scaled - idx;

                fixed4 a, b;
                if (idx == 0) { a = _Color0; b = _Color1; }
                else if (idx == 1) { a = _Color1; b = _Color2; }
                else if (idx == 2) { a = _Color2; b = _Color3; }
                else if (idx == 3) { a = _Color3; b = _Color4; }
                else { a = _Color4; b = _Color5; }

                return lerp(a, b, frac);
            }

            fixed4 frag (v2f i) : SV_Target {
                // UNIFIED FLIP LOGIC: Always mirror X (matches OpenCV FlipMode.X), 
                // and conditionally flip Y when _FlipDisplay180 is enabled (matches FlipMode.XY).
                float2 displayUV = float2(_FlipX > 0.5 ? 1.0 - i.uv.x : i.uv.x,
                _FlipY > 0.5 ? 1.0 - i.uv.y : i.uv.y);

                // --- 1. GLOBAL BOUNDS CHECK ---
                if (_UseBounds > 0.5)
                {
                    if (i.uv.x < _Bounds.x || i.uv.x > _Bounds.z || 
                        i.uv.y < _Bounds.y || i.uv.y > _Bounds.w)
                    {
                        return fixed4(0, 0, 0, 1.0);
                    }
                }

                // Opaque black base color by default
                fixed4 finalColor = fixed4(0, 0, 0, 1.0);

                // --- 2. BASE FEED LAYER ---
                if (_IsDepthMode > 0.5) { 
                    if (_PostProcessEnabled > 0.5) {
                        float rawDepth = tex2D(_MainTex, displayUV).r * 65535.0;

                        bool inRange = (rawDepth >= _DepthMin && rawDepth <= _DepthMax);

                        if (inRange) {
                            float t = (rawDepth - _DepthMin) / max(1.0, (_DepthMax - _DepthMin));
                            float elevation = 1.0 - saturate(t);
                            finalColor = ElevationColor(elevation);
                        } else {
                            finalColor = fixed4(0, 0, 0, 1.0);
                        }
                    } else {
                        float val = tex2D(_MainTex, displayUV).r;
                        finalColor = fixed4(val, val, val, 1.0);
                    }
                } 
                else if (_IsColorMode > 0.5) {
                    finalColor = tex2D(_MainTex, displayUV);
                }
                else {
                    // Infrared / Monochromatic Mode
                    fixed4 rawSample = tex2D(_MainTex, displayUV);
                    float infraVal = rawSample.r;
                    finalColor = fixed4(infraVal, infraVal, infraVal, 1.0);
                }

                // --- 3. GAME SCENE LAYER ---
                if (_ShowGameScene > 0.5) {
                    fixed4 gameColor = tex2D(_GameTex, displayUV);
                    float effGameAlpha = gameColor.a * _GameSceneAlpha;
                    if (effGameAlpha > 0.01) {
                        finalColor.rgb = lerp(finalColor.rgb, gameColor.rgb, effGameAlpha);
                    }
                }

                return finalColor * i.color;
            }
            ENDCG
        }
    }
}