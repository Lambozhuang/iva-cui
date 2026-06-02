// Minimal unlit, alpha-blended fill used by ScreenFader for VR comfort fades.
//
// Key render state:
//   ZTest Always  — draws over EVERYTHING (scene geometry, controller models,
//                   hands), so the fade has no poke-through holes. A UGUI Image
//                   (ZTest LEqual) would let anything closer than the canvas
//                   punch through; this does not.
//   ZWrite Off    — never writes depth, so it can't disturb later passes.
//   Cull Off      — renders regardless of which way the quad faces the camera.
//   Queue Overlay — drawn after the transparent queue, i.e. last.
//
// URP compatibility: the single Pass has no LightMode tag, which URP treats as
// SRPDefaultUnlit and renders (same path Unlit/Color and Sprites/Default use).
// No URP-specific HLSL needed for a solid colour.
//
// Stereo: the macros below make it render correctly under Quest's single-pass
// instanced stereo (each eye is an instance). For a huge full-view black quad
// this is belt-and-suspenders — at scale 30 / 1 m the 3 cm eye offset can't
// expose an edge — but it's cheap and keeps the shader correct for any future
// non-black use.
Shader "QoE/ScreenFade"
{
    Properties
    {
        _Color ("Color", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Overlay" "IgnoreProjector"="True" "ForceNoShadowCasting"="True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return _Color;
            }
            ENDCG
        }
    }
}
