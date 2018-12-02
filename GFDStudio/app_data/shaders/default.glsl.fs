﻿
#version 330 core

// in
in vec3 fPosition;
in vec3 fNormal;
in vec2 fTex0;
in vec4 fColor0;

// out
out vec4 fColor;

// samplers
uniform sampler2D sDiffuse;

// material properties
uniform bool matHasDiffuse;
uniform vec4 matAmbient;
uniform vec4 matDiffuse;
uniform vec4 matSpecular;
uniform vec4 matEmissive;
uniform bool matHasAlphaTransparency;

float luma(vec3 color) {
  return dot(color, vec3(0.299, 0.587, 0.114));
}

float luma(vec4 color) {
  return dot(color.rgb, vec3(0.299, 0.587, 0.114));
}

void main()
{
	if ( matHasDiffuse )
	{
        vec4 diffuseColor = texture2D( sDiffuse, fTex0 );
        if ( matHasAlphaTransparency && diffuseColor.a < 0.1 )
            discard;

		vec3 lightDirection = vec3( 0.0, 0.0, -1.0 );
		vec3 eyePos = normalize( -fPosition );
		vec3 reflection = normalize( -reflect( lightDirection, fNormal ) );

		// Calculate ambient
		vec4 ambient = clamp( matAmbient, 0.0f, 0.025f );

		// Calculate diffuse
		vec4 diffuse = matDiffuse * max( dot( fNormal, lightDirection), 0.0 );

		// Calculate specular
		//vec4 specular = matSpecular * pow( max( dot( reflection, eyePos ), 0.0 ), 0.3 * 0.25 );

		// Calculate accumated color so far
		vec4 accumulatedColor = diffuseColor + ambient + diffuse; // + specular;

		// Calculate rim light
		float rimWidth = 0.65;
        float rimPower = 0.9;	
		float rimIntensity = rimWidth - max( dot( eyePos, fNormal ), 0.0 );
		rimIntensity = max( 0.0, rimIntensity * rimPower ); // ignore rim light if negative
		
		/*
		float brightness = clamp( luma( accumulatedColor ), 0.0, 1.0 );
		rimIntensity = rimIntensity / ( ( brightness / 0.5 ) * 2.0 ); 
		*/

		vec4 rimColor = vec4( rimIntensity * matAmbient.xyz, 1.0 );

		// Calculate final fment color
		fColor = accumulatedColor + rimColor;
	}
	else
	{
		fColor = matDiffuse;
	}
}