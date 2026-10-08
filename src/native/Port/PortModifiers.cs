#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Port;

namespace PortContent;

// Extra custom run modifiers (singleplayer only), listed after the game's own on the Custom run screen. They are
// registered with ModelDb.Inject after the game's init, so the multiplayer content hash stays the same as on PC; a
// multiplayer peer couldn't deserialize them, so they're hidden outside singleplayer. Saves keep their ids
// ("MODIFIER.SUPERCHARGED"); the PC game loads unknown modifier ids as DeprecatedModifier (no effect).
static class PortModifiers
{
	// Applied in this order, after the game's modifiers (Neow offers modifier options in list order).
	static readonly Type[] All = { typeof(Supercharged), typeof(PerfectedDeck) };

	static readonly Dictionary<string, string> Text = new()
	{
		["SUPERCHARGED.title"] = "Supercharged",
		["SUPERCHARGED.description"] = "Gain [blue]20[/blue] additional [gold]Energy[/gold] at the start of each turn.",
		["PERFECTED_DECK.title"] = "Perfected Deck",
		["PERFECTED_DECK.description"] = "After the other modifiers, [gold]Transform[/gold] every card in your deck into [gold]Perfected Strike+[/gold].",
		["PERFECTED_DECK.neowDescription"] = "[gold]Transform[/gold] every card in your deck into [gold]Perfected Strike+[/gold].",
		["PERFECTED_DECK.infoText"] = "Your deck has been perfected.",
	};

	[ModuleInitializer]
	internal static void Init()
	{
		PortHooks.AfterEssentialInit = Register;
		PortHooks.CustomRunModifiers = bad => bad.Concat(All.Select(t => (ModifierModel)ModelDb.GetById<ModifierModel>(ModelDb.GetId(t)))).ToList();
		PortHooks.CustomRunModifiersInitialize = OnModifiersListInitialize;
	}

	static void Register()
	{
		foreach (var type in All)
			ModelDb.Inject(type);
		AddText();
		LocManager.Instance.SubscribeToLocaleChange(AddText); // other languages show the English text
		GD.Print("[PORT] custom run modifiers registered: ", string.Join(", ", All.Select(ModelDb.GetId)));
	}

	static void AddText()
	{
		try
		{
			LocManager.Instance.GetTable("modifiers").MergeWith(Text);
		}
		catch (Exception e)
		{
			GD.PrintErr("[PORT] custom run modifier text: " + e.Message);
		}
	}

	static void OnModifiersListInitialize(NCustomRunModifiersList list, MultiplayerUiMode mode)
	{
		bool singleplayer = mode == MultiplayerUiMode.Singleplayer;
		foreach (var node in list.FindChildren("*", "", true, false))
		{
			if (node is NRunModifierTickbox tickbox && tickbox.Modifier != null && Array.IndexOf(All, tickbox.Modifier.GetType()) >= 0)
			{
				if (!singleplayer && tickbox.IsTicked)
					tickbox.IsTicked = false;
				tickbox.Visible = singleplayer;
			}
		}
	}
}

// +20 Energy every turn (raises max energy, which the game refills each turn; like energy relics).
public sealed class Supercharged : ModifierModel
{
	public const int ExtraEnergy = 20;

	protected override string IconPath => "res://images/atlases/power_atlas.sprites/energy_next_turn_power.tres";

	public override decimal ModifyMaxEnergy(Player player, decimal amount) => amount + ExtraEnergy;
}

// Transform the whole deck into Perfected Strike+, after every other modifier.
// Modifiers that build the starting deck do it through Neow options, which Neow offers in the run's modifier order;
// this one is listed (and so applied) last. Cards the game doesn't allow to be transformed (unremovable curses) stay.
public sealed class PerfectedDeck : ModifierModel
{
	const string IconRes = "res://port/perfected_deck_icon.tres";
	const string ArtRes = "res://images/atlases/card_atlas.sprites/ironclad/perfected_strike.tres";
	static AtlasTexture? _icon;

	public override LocString NeowOptionDescription => new("modifiers", "PERFECTED_DECK.neowDescription");

	public override IEnumerable<IHoverTip> HoverTips => new[] { HoverTipFactory.FromCard<PerfectedStrike>(upgrade: true) };

	// A square crop of the card art (modifier icons are square; the game has no icon for this one).
	protected override string IconPath
	{
		get
		{
			if (_icon == null && ResourceLoader.Load(ArtRes) is AtlasTexture art)
			{
				var r = art.Region;
				float side = Math.Min(r.Size.X, r.Size.Y);
				_icon = new AtlasTexture
				{
					Atlas = art.Atlas,
					Region = new Rect2(r.Position.X + (r.Size.X - side) / 2, r.Position.Y + (r.Size.Y - side) / 2, side, side),
				};
				_icon.TakeOverPath(IconRes);
			}
			return _icon != null ? IconRes : base.IconPath;
		}
	}

	public override Func<Task>? GenerateNeowOption(EventModel eventModel) =>
		() => eventModel.Owner is { } player ? Perfect(player) : Task.CompletedTask;

	static async Task Perfect(Player player)
	{
		var transformations = player.Deck.Cards
			.Where(c => c.IsTransformable && !(c is PerfectedStrike && c.IsUpgraded))
			.Select(c =>
			{
				var replacement = c.CardScope!.CreateCard<PerfectedStrike>(c.Owner);
				CardCmd.Upgrade(replacement, CardPreviewStyle.None);
				return new CardTransformation(c, replacement);
			})
			.ToList();
		if (transformations.Count == 0)
			return;
		var results = (await CardCmd.Transform(transformations, null, CardPreviewStyle.None)).ToList();
		if (LocalContext.IsMe(player))
			NSimpleCardsViewScreen.ShowScreen(results, new LocString("modifiers", "PERFECTED_DECK.infoText"));
	}
}
