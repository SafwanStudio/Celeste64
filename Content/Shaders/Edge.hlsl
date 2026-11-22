// Vertex Shader

cbuffer VertexUniforms : register(b0, space1)
{
    float4x4 Matrix;
};

struct VsInput
{
    float2 Position : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float4 Color : TEXCOORD2;
    float4 Type : TEXCOORD4;
};

struct VsOutput
{
    float2 TexCoord : TEXCOORD0;
    float4 Color : TEXCOORD1;
    float4 Type : TEXCOORD4;
    float4 Position : SV_Position;
};

VsOutput vertex_main(VsInput input)
{
    VsOutput output;
    output.TexCoord = input.TexCoord;
    output.Color = input.Color;
    output.Type = input.Type;
    output.Position = mul(Matrix, float4(input.Position, 0.0, 1.0));
    return output;
}

// Fragment Shader

cbuffer FragmentUniforms : register(b0, space3)
{
    float4 EdgeColor;
	float2 Pixel;
};

Texture2D Texture : register(t0, space2);
SamplerState TextureSampler : register(s0, space2);

Texture2D Depth : register(t1, space2);
SamplerState DepthSampler : register(s1, space2);

float depth(float2 uv)
{
	return Depth.Sample(DepthSampler, uv).r;
}

float4 fragment_main(VsOutput input) : SV_Target0
{
	// get depth and adjacent depth values
	float it = depth(input.TexCoord);
	float other = 
		depth(input.TexCoord + float2(Pixel.x, 0)) * 0.25 +
		depth(input.TexCoord + float2(-Pixel.x, 0)) * 0.25 +
		depth(input.TexCoord + float2(0, Pixel.y)) * 0.25 +
		depth(input.TexCoord + float2(0, -Pixel.y)) * 0.25;
	
	// more edge the closer to the screen
	float edge = step(0.001, other - it);

	// base color
	float4 col = Texture.Sample(TextureSampler, input.TexCoord);
	float3 base = col.rgb;

	// soft bloom: average neighbors, keep only the bright part
	float3 blur =
		Texture.Sample(TextureSampler, input.TexCoord + float2( Pixel.x, 0)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2(-Pixel.x, 0)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2(0,  Pixel.y)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2(0, -Pixel.y)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2( Pixel.x,  Pixel.y)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2(-Pixel.x,  Pixel.y)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2( Pixel.x, -Pixel.y)).rgb +
		Texture.Sample(TextureSampler, input.TexCoord + float2(-Pixel.x, -Pixel.y)).rgb;
	blur *= 0.125;
	float3 glow = max(blur - float3(0.62, 0.62, 0.62), float3(0, 0, 0));
	base += glow * float3(1.0, 0.92, 0.80) * 0.55;

	// edge color: mix the outline color with a darkened version of the surface
	float3 edgeCol = lerp(EdgeColor.rgb, base * 0.35, 0.35);
	float3 res = lerp(base, edgeCol, edge * 0.70);

	// color grade: warm highlights, cool shadows, a little saturation
	float luma = dot(res, float3(0.299, 0.587, 0.114));
	res = lerp(float3(luma, luma, luma), res, 1.12);
	res += float3(0.020, 0.008, -0.018) * smoothstep(0.5, 1.0, luma);
	res += float3(-0.012, 0.000, 0.022) * (1.0 - smoothstep(0.0, 0.45, luma));

	// vignette
	float2 q = input.TexCoord - float2(0.5, 0.5);
	float vig = 1.0 - smoothstep(0.35, 0.85, length(q * float2(1.0, 1.15)));
	res *= lerp(0.80, 1.0, vig);

	return float4(saturate(res), col.a);
}
        