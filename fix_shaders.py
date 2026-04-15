import os
import re

search_dir = r"C:\Users\victo\XRexplorer\Assets\Nanover\Visualisation\Shader"

for root, dirs, files in os.walk(search_dir):
    for file in files:
        if file.endswith(".cginc") or file.endswith(".shader"):
            filepath = os.path.join(root, file)
            with open(filepath, "r", encoding="utf-8") as f:
                content = f.read()
            
            # Use regex to do whole word replacement
            new_content = re.sub(r'\bObjectToWorld\b', '_NanoverObjectToWorld', content)
            new_content = re.sub(r'\bWorldToObject\b', '_NanoverWorldToObject', new_content)
            new_content = re.sub(r'\bObjectToWorldInverseTranspose\b', '_NanoverObjectToWorldInverseTranspose', new_content)
            
            # Fix determinant signature error by casting to float4x4
            # We specifically look for determinant(_NanoverObjectToWorld) and determinant(unity_ObjectToWorld)
            new_content = re.sub(r'determinant\(\s*_NanoverObjectToWorld\s*\)', 'determinant((float4x4)_NanoverObjectToWorld)', new_content)
            new_content = re.sub(r'determinant\(\s*unity_ObjectToWorld\s*\)', 'determinant((float4x4)unity_ObjectToWorld)', new_content)
            new_content = re.sub(r'determinant\(\s*_BaseObjectToWorld\s*\)', 'determinant((float4x4)_BaseObjectToWorld)', new_content)
            
            if new_content != content:
                with open(filepath, "w", encoding="utf-8") as f:
                    f.write(new_content)
                print(f"Updated {filepath}")
