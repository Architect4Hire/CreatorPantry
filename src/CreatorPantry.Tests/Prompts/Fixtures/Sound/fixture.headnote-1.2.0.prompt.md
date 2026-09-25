---
{
  "id": "fixture.headnote",
  "version": "1.2.0",
  "outputSchemaVersion": "fixture.headnote.v2",
  "safetyClass": "CulinaryAdvice",
  "inputs": [
    { "name": "recipeTitle", "required": true, "description": "Creator-entered title, as entered." },
    { "name": "servings", "required": false, "description": "Yield, when the recipe declares one." }
  ],
  "bodyChecksum": "sha256:b47985ef3950f4fc65c523a66ebf428f408121e42889b10aad6ae8b7de3662c6"
}
---
Suggest a headnote for {{recipeTitle}}.
Serves: {{servings}}
