using System.IO;
using System.Runtime.Serialization;

using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;

using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;

// NOTE: Namespace must NOT contain "SwarmUI" (this is reserved for built-ins)
namespace SDPerturbedAttentionExtension;

[Newtonsoft.Json.JsonConverter(typeof(StringEnumConverter))]
public enum UnetBlock
{
    [EnumMember(Value = "input")]
    Input = 0,
    [EnumMember(Value = "middle")]
    Middle = 1,
    [EnumMember(Value = "output")]
    Output = 2,
}

[Newtonsoft.Json.JsonConverter(typeof(StringEnumConverter))]
public enum RescaleMode
{
    [EnumMember(Value = "full")]
    Full = 0,
    [EnumMember(Value = "partial")]
    Partial = 1,
    [EnumMember(Value = "snf")]
    SNF = 2,
}

// NOTE: Classname must match filename
public class SDPerturbedAttentionExtension : Extension // extend the "Extension" class in Swarm Core
{
    protected const string FeatureId = "perturbedattention";

    // Generally define parameters as "public static" to make them easy to access in other code, actual registration is done in OnInit
    public static T2IRegisteredParam<float> PerturbedAttentionScale, PerturbedAttentionAdaptiveScale, PerturbedAttentionSigmaStart, PerturbedAttentionSigmaEnd, PerturbedAttentionRescale;

    public static T2IRegisteredParam<string> PerturbedAttentionUnetBlock;
    public static T2IRegisteredParam<string> PerturbedAttentionRescaleMode;

    public static T2IRegisteredParam<int> PerturbedAttentionUnetBlockID;

    public static T2IParamGroup PerturbedAttentionParameters;

    public static T2IRegisteredParam<float> SlidingWindowScale, SlidingWindowOverlap, SlidingWindowSigmaStart, SlidingWindowSigmaEnd;

    public static T2IRegisteredParam<int> SlidingWindowTileWidth, SlidingWindowTileHeight;

    public static T2IParamGroup SlidingWindowParameters;

    // OnInit is called when the extension is loaded, and is the general place to register most things
    public override void OnInit()
    {
        base.OnInit();

        // Add the JS file, which manages the install buttons for the comfy nodes
        ScriptFiles.Add($"assets/{FeatureId}.js");

        ComfyUIBackendExtension.NodeToFeatureMap["PerturbedAttention"] = FeatureId;
        ComfyUIBackendExtension.NodeToFeatureMap["SlidingWindowGuidanceAdvanced"] = FeatureId;

        // Add required custom node as installable feature
        InstallableFeatures.RegisterInstallableFeature(new("PerturbedAttention", FeatureId, "https://github.com/pamparamm/sd-perturbed-attention", "pamparamm", "This will install the sd-perturbed-attention ComfyUI nodes developed by pamparamm.\nDo you wish to install?"));

        // Prevents install button from being shown during backend load if it looks like it was installed
        // it will appear if the backend loads and the backend reports it's not installed
        if (Directory.Exists(Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, $"{ComfyUIBackendExtension.Folder}/DLNodes/sd-perturbed-attention")))
        {
            ComfyUIBackendExtension.FeaturesSupported.UnionWith([FeatureId]);
            ComfyUIBackendExtension.FeaturesDiscardIfNotFound.UnionWith([FeatureId]);
        }


        PerturbedAttentionParameters = new(
            "Perturbed Attention Guidance (Advanced)",
            Description: string.Join("\n", [
                "Based on the technique used by Self-Attention Guidance, an enhanced version.",
                "Replaces model's properly selected self-attention maps with Identity Matrix to guide model away from bad image structure.",
            ]),
            Toggles: true,
            Open: false,
            IsAdvanced: true,
            CanShrink: true
        );

        SlidingWindowParameters = new(
            "Sliding Window Guidance",
            Description: string.Join("\n", [
                "Use sliding windows to generate multiple cropped tiles from image data,",
                "aggregate them with averaged overlap into a negative noise predictor.",
                "With the help of a formula, positive noise predictor and negative noise predictor is combined and used for guidance.",
                " - Set l=k=2s or l=k=3s, then you will have a good time.",
                " - Process time depends on tile number, more means longer.",
            ]),
            Toggles: true,
            Open: false,
            IsAdvanced: true,
            CanShrink: true
        );

        // "scale": ("FLOAT", {"default": 3.0, "min": 0.0, "max": 100.0, "step": 0.1, "round": 0.01}),
        PerturbedAttentionScale = T2IParamTypes.Register<float>(new(
            "Perturbed Attention Scale",
            string.Join("\n", [
                "1-decent 4-moderate 7-strong",
                " - go beyond 7 you'll start to see burnt spot.",
                " - push it further to 15 will definitely burn up image if no extra measure is taken.",
            ]),
            "3.0",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,

            // Check your IDE's completions here, there's tons of additional options. Look inside the T2IParamTypes to see how other params are registered.
            Min: 0,
            Max: 100,
            Step: 0.1,
            Type: T2IParamDataType.DECIMAL,
            ViewType: ParamViewType.SLIDER,
            IsAdvanced: false
        ));

        // "adaptive_scale": ("FLOAT", {"default": 0.0, "min": 0.0, "max": 1.0, "step": 0.001, "round": 0.0001}),
        PerturbedAttentionAdaptiveScale = T2IParamTypes.Register<float>(new(
            "Perturbed Attention Adaptive Scale",
            "Dampens PAG, ranging from the last step towards first step based on how big this percentage is.",
            "0.0",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,

            // Check your IDE's completions here, there's tons of additional options. Look inside the T2IParamTypes to see how other params are registered.
            Min: 0,
            Max: 1,
            Step: 0.001,
            Type: T2IParamDataType.DECIMAL,
            ViewType: ParamViewType.SLIDER,
            IsAdvanced: true
        ));

        // "unet_block": (["input", "middle", "output"], {"default": "middle"}),
        PerturbedAttentionUnetBlock = T2IParamTypes.Register<string>(new(
            "Perturbed Attention UNet Block",
            "middle is suggested",
            "middle",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,
            Type: T2IParamDataType.DROPDOWN,
            GetValues: a => [.. Enum.GetNames(typeof(UnetBlock))],
            SharpType: typeof(UnetBlock),
            IsAdvanced: true
        ));

        // "unet_block_id": ("INT", {"default": 0}),
        PerturbedAttentionUnetBlockID = T2IParamTypes.Register<int>(new(
            "Perturbed Attention UNet Block ID",
            "leave it 0 if clueless",
            "0",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,
            Min: 0,
            IsAdvanced: true
        ));

        // "sigma_start": ("FLOAT", {"default": -1.0, "min": -1.0, "max": 10000.0, "step": 0.01, "round": False}),
        PerturbedAttentionSigmaStart = T2IParamTypes.Register<float>(new(
            "Perturbed Attention Sigma Start",
            string.Join("\n", [
                "activate guidance between sigma_end_and sigma_start.",
                "in usual case, node is enabled from step No. sigma_end to step No. sigma_start.",
                "set them both negative disable this feature.",
                "by only setting sigma_start negative, guidance ends at last step.",
                "by only setting sigma_end negative, guidance start from the first step.",
                "flipped value (sigma_start<sigma_end) will stop the node from taking effect.",
            ]),
            "-1.0",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,
            Min: -1,
            Max: 10000,
            Step: 0.01,
            IsAdvanced: true
        ));

        // "sigma_end": ("FLOAT", {"default": -1.0, "min": -1.0, "max": 10000.0, "step": 0.01, "round": False}),
        PerturbedAttentionSigmaEnd = T2IParamTypes.Register<float>(new(
            "Perturbed Attention Sigma End",
            string.Join("\n", [
                "activate guidance between sigma_end_and sigma_start.",
                "in usual case, node is enabled from step No. sigma_end to step No. sigma_start.",
                "set them both negative disable this feature.",
                "by only setting sigma_start negative, guidance ends at last step.",
                "by only setting sigma_end negative, guidance start from the first step.",
                "flipped value (sigma_start<sigma_end) will stop the node from taking effect.",
            ]),
            "-1.0",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,
            Min: -1,
            Max: 10000,
            Step: 0.01,
            IsAdvanced: true
        ));

        // "rescale": ("FLOAT", {"default": 0.0, "min": 0.0, "max": 1.0, "step": 0.01}),
        PerturbedAttentionRescale = T2IParamTypes.Register<float>(new(
            "Perturbed Attention Rescale",
            "Based on the algorithm from Common Diffusion Noise Schedules and Sample Steps are Flawed, enlarging it reduces overexposure (burns). Does nothing when rescale_mode=snf.",
            "0.0",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,
            Min: 0,
            Max: 1,
            Step: 0.01,
            IsAdvanced: true
        ));

        // "rescale_mode": (["full", "partial", "snf"], {"default": "full"}),
        PerturbedAttentionRescaleMode = T2IParamTypes.Register<string>(new(
            "Perturbed Attention Rescale Mode",
            string.Join("\n", [
                "full / partial / snf",
                " - full mode considers both CFG and this guidance.",
                " - partial mode only considers guidance from this node.",
                " - snf is Saliency-adaptive Noise Fusion from High-fidelity Person-centric Subject-to-Image Synthesis used in subject-scene fusion stage.",
            ]),
            "full",
            Toggleable: true,
            Group: PerturbedAttentionParameters,
            FeatureFlag: FeatureId,
            Type: T2IParamDataType.DROPDOWN,
            SharpType: typeof(RescaleMode),
            GetValues: a => [.. Enum.GetNames(typeof(RescaleMode))],
            IsAdvanced: true
        ));

        // "scale": ("FLOAT", {"default": 5.0, "min": 0.0, "max": 100.0, "step": 0.1, "round": 0.01}),
        SlidingWindowScale = T2IParamTypes.Register<float>(new(
            "Scale",
            string.Join("\n", [
                "(w): the paper said it is optimal around 0.2.",
                " - same as PAG, it has 7/15 temperature border, staying there takes the risk of burning.",
            ]),
            "5.0",
            Toggleable: true,
            Group: SlidingWindowParameters,
            FeatureFlag: FeatureId,

            // Check your IDE's completions here, there's tons of additional options. Look inside the T2IParamTypes to see how other params are registered.
            Min: 0,
            Max: 100,
            Step: 0.1,
            Type: T2IParamDataType.DECIMAL,
            ViewType: ParamViewType.SLIDER,
            IsAdvanced: false
        ));

        // "tile_width": ("INT", {"default": 768, "min": 16, "max": 16384, "step": 8}),
        SlidingWindowTileWidth = T2IParamTypes.Register<int>(new(
            "Sliding Window Tile Width",
            "(l): the width of cropped tiles.",
            "768",
            Toggleable: true,
            Group: SlidingWindowParameters,
            FeatureFlag: FeatureId,
            Min: 16,
            Max: 16384,
            Step: 8,
            IsAdvanced: true
        ));

        // "tile_height": ("INT", {"default": 768, "min": 16, "max": 16384, "step": 8}),
        SlidingWindowTileHeight = T2IParamTypes.Register<int>(new(
            "Sliding Window Tile Height",
            "(k): the height of cropped tiles.",
            "768",
            Toggleable: true,
            Group: SlidingWindowParameters,
            FeatureFlag: FeatureId,
            Min: 16,
            Max: 16384,
            Step: 8,
            IsAdvanced: true
        ));

        // "tile_overlap": ("INT", {"default": 256, "min": 16, "max": 16384, "step": 8}),
        SlidingWindowOverlap = T2IParamTypes.Register<float>(new(
            "Sliding Window Overlap",
            "(s): the size of overlap (pixel), sensitive.",
            "256",
            Toggleable: true,
            Group: SlidingWindowParameters,
            FeatureFlag: FeatureId,
            Min: 16,
            Max: 16384,
            Step: 8,
            IsAdvanced: true
        ));

        // "sigma_start": ("FLOAT", {"default": -1.0, "min": -1.0, "max": 10000.0, "step": 0.01, "round": False}),
        SlidingWindowSigmaStart = T2IParamTypes.Register<float>(new(
            "Sliding Window Sigma Start",
            "The step to start guidance from",
            "-1.0",
            Toggleable: true,
            Group: SlidingWindowParameters,
            FeatureFlag: FeatureId,
            Min: -1,
            Max: 10000,
            Step: 0.01,
            IsAdvanced: true
        ));

        // "sigma_end": ("FLOAT", {"default": 5.42, "min": -1.0, "max": 10000.0, "step": 0.01, "round": False}),
        SlidingWindowSigmaEnd = T2IParamTypes.Register<float>(new(
            "Sliding Window Sigma End",
            "The step to end guidance at",
            "5.42",
            Toggleable: true,
            Group: SlidingWindowParameters,
            FeatureFlag: FeatureId,
            Min: -1,
            Max: 10000,
            Step: 0.01,
            IsAdvanced: true
        ));

        // AddStep for custom Comfy steps. Can also use AddModelGenStep for custom model configuration steps.
        WorkflowGenerator.AddModelGenStep(g =>
        {
            // Generally always check that your parameter exists before doing anything (so you don't infect unrelated generations unless the user wants your feature running)
            if (g.UserInput.TryGet(PerturbedAttentionScale, out float scale))
            {
                JObject nodeDef = new() {
                    ["model"] = g.LoadingModel,
                    ["scale"] = scale,
                };

                // string pagNode = g.CreateNode("PerturbedAttention", new JObject()
                // {
                //     And configure all the inputs to that node...
                //     ["model"]          = g.LoadingModel,
                //     ["scale"]          = scale,
                //     ["adaptive_scale"] = g.UserInput.Get(PerturbedAttentionAdaptiveScale, 0),
                //     ["unet_block"]     = g.UserInput.Get(PerturbedAttentionUnetBlock).ToString(),
                //     ["unet_block_id"]  = g.UserInput.Get(PerturbedAttentionUnetBlockID),
                //     ["sigma_start"]    = g.UserInput.Get(PerturbedAttentionSigmaStart),
                //     ["sigma_end"]      = g.UserInput.Get(PerturbedAttentionSigmaEnd),
                //     ["rescale"]        = g.UserInput.Get(PerturbedAttentionRescale),
                //     ["rescale_mode"]   = g.UserInput.Get(PerturbedAttentionRescaleMode).ToString(),
                // });

                if (g.UserInput.TryGet(PerturbedAttentionAdaptiveScale, out float adaptiveScale)) {
                    nodeDef["adaptive_scale"] = adaptiveScale;
                }

                if (g.UserInput.TryGet(PerturbedAttentionUnetBlock, out string unetBlock)) {
                    nodeDef["unet_block"] = unetBlock.ToString();
                }

                if (g.UserInput.TryGet(PerturbedAttentionUnetBlockID, out int unetBlockID)) {
                    nodeDef["unet_block_id"] = unetBlockID;
                }

                if (g.UserInput.TryGet(PerturbedAttentionSigmaStart, out float sigmaStart)) {
                    nodeDef["sigma_start"] = sigmaStart;
                }

                if (g.UserInput.TryGet(PerturbedAttentionSigmaEnd, out float sigmaEnd)) {
                    nodeDef["sigma_end"] = sigmaEnd;
                }

                if (g.UserInput.TryGet(PerturbedAttentionRescale, out float rescale)) {
                    nodeDef["rescale"] = rescale;
                }

                if (g.UserInput.TryGet(PerturbedAttentionRescaleMode, out string rescaleMode)) {
                    nodeDef["rescale_mode"] = rescaleMode;
                }


                string pagNode = g.CreateNode("PerturbedAttention", nodeDef);
                g.LoadingModel = [pagNode, 0];
            }

            if (g.UserInput.TryGet(SlidingWindowScale, out float slidingWindowScale))
            {
                JObject nodeDef = new() {
                    ["model"] = g.LoadingModel,
                    ["scale"] = slidingWindowScale,
                };

                if (g.UserInput.TryGet(SlidingWindowTileWidth, out int tileWidth)) {
                    nodeDef["tile_width"] = tileWidth;
                }

                if (g.UserInput.TryGet(SlidingWindowTileHeight, out int tileHeight)) {
                    nodeDef["tile_height"] = tileHeight;
                }

                if (g.UserInput.TryGet(SlidingWindowOverlap, out float overlap)) {
                    nodeDef["overlap"] = overlap;
                }

                if (g.UserInput.TryGet(SlidingWindowSigmaStart, out float sigmaStart)) {
                    nodeDef["sigma_start"] = sigmaStart;
                }

                if (g.UserInput.TryGet(SlidingWindowSigmaEnd, out float sigmaEnd)) {
                    nodeDef["sigma_end"] = sigmaEnd;
                }

                string pagNode = g.CreateNode("SlidingWindowGuidanceAdvanced", nodeDef);
                g.LoadingModel = [pagNode, 0];
            }

            // The priority value determines where in the workflow this will process.
            // You can technically just use a late priority and then just modify the workflow at will, but it's best to run at the appropriate time.
            // Check the source of WorkflowGenerator to see what priorities are what.
            // In this case, the final save image step is at priority of "10", so we run at "9", ie just before that.
            // (You can use eg 9.5 or 9.999 if you think something else is running at 9 and you need to be after it).
        }, 9);
    }
}
