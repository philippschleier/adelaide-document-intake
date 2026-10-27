using System.Runtime.CompilerServices;

// Expose the internal prompt-template hashes (VisionTranscriber.TemplateHash, AzureOpenAiExtractor.TemplateHash)
// to the test project only, so PromptHashPinTests can pin them to known values. No runtime effect.
[assembly: InternalsVisibleTo("Adelaide.Core.Tests")]
