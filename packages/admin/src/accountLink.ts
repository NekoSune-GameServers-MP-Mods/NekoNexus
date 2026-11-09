/*
 * Cross-save: link a Steam account with an email (Android / Standalone) game account.
 *
 * The player signs in to the website with Steam, proves they own the email account with its
 * email + password, and picks which profile to keep (the "primary"). The other profile is
 * deleted and both logins then open the primary profile:
 *   - Steam (PC)                 -> SteamMember.Cmid = primary
 *   - email + password (Android, Standalone, later iOS/macOS) -> UserAccount of the primary
 *
 * Everything happens in one database transaction. Moderation records, player reports and payment
 * orders of the deleted profile are moved to the primary instead of deleted, so merging can't be
 * used to shed a ban and purchase records are kept.
 */

import models from '@festivaldev/nekonexus-models';
import bcrypt from 'bcryptjs';
import { Op, type Sequelize } from 'sequelize';

export interface LinkCandidate {
  cmid: number;
  name: string;
  level: number;
  xp: number;
  credits: number;
  points: number;
  items: number;
}

export interface LinkCheckResult {
  ok: boolean;
  error?: string;
  steam?: LinkCandidate;
  email?: LinkCandidate;
}

async function describe(cmid: number): Promise<LinkCandidate> {
  const [profile, stats, wallet, items] = await Promise.all([
    models.PublicProfile.findByPk(cmid, { raw: true }) as Promise<any>,
    models.PlayerStatistics.findOne({ where: { Cmid: cmid }, raw: true }) as Promise<any>,
    models.MemberWallet.findOne({ where: { Cmid: cmid }, raw: true }) as Promise<any>,
    models.PlayerInventoryItem.count({ where: { Cmid: cmid } }),
  ]);
  return {
    cmid,
    name: profile?.Name ?? `Player ${cmid}`,
    level: Number(stats?.Level) || 1,
    xp: Number(stats?.Xp) || 0,
    credits: Number(wallet?.Credits) || 0,
    points: Number(wallet?.Points) || 0,
    items: items ?? 0,
  };
}

/**
 * Verify the email account and that the two accounts may be linked. Does not change anything.
 * `steamCmid` / `steamId` come from the signed-in website session, never from the request body.
 */
export async function checkLink(steamCmid: number, steamId: string, email: string, password: string): Promise<LinkCheckResult & { emailCmid?: number }> {
  const address = String(email ?? '').trim();
  if (!address || !password) return { ok: false, error: 'Enter the email and password of your game account.' };

  const account: any = await models.UserAccount.findOne({ where: { EmailAddress: address }, raw: true });
  // Same message for unknown email and wrong password, so the form can't be used to probe emails.
  if (!account?.Password || !bcrypt.compareSync(String(password), account.Password)) {
    return { ok: false, error: 'Email or password is incorrect.' };
  }

  const emailCmid = Number(account.Cmid);
  if (emailCmid === steamCmid) return { ok: false, error: 'This email account is already your Steam account.' };

  const steamMember: any = await models.SteamMember.findByPk(steamId, { raw: true });
  if (!steamMember || Number(steamMember.Cmid) !== steamCmid) return { ok: false, error: 'Your Steam sign-in is out of date. Sign in again.' };

  const otherSteam = await models.SteamMember.count({ where: { Cmid: emailCmid } });
  if (otherSteam > 0) return { ok: false, error: 'That email account is already linked to a Steam account.' };

  return { ok: true, emailCmid, steam: await describe(steamCmid), email: await describe(emailCmid) };
}

/** Link the accounts, keeping `primary` ('steam' | 'email') and deleting the other profile. */
export async function linkAccounts(
  sequelize: Sequelize,
  steamCmid: number,
  steamId: string,
  email: string,
  password: string,
  primary: 'steam' | 'email',
): Promise<{ ok: boolean; error?: string; cmid?: number }> {
  const check = await checkLink(steamCmid, steamId, email, password);
  if (!check.ok || !check.emailCmid) return { ok: false, error: check.error };

  const emailCmid = check.emailCmid;
  const keep = primary === 'email' ? emailCmid : steamCmid;
  const drop = primary === 'email' ? steamCmid : emailCmid;

  // Both accounts must be offline: live sessions would keep writing to the deleted profile.
  const online = await models.ActivePlayer.count({ where: { Cmid: { [Op.in]: [keep, drop] } } });
  if (online > 0) return { ok: false, error: 'Log out of the game on every device first, then try again.' };

  // A clan led by the profile being deleted would be left without a leader.
  const ledClans: any[] = (await models.Clan.findAll({ where: { OwnerCmid: drop }, raw: true })) as any[];
  for (const clan of ledClans) {
    const members = await (models.ClanMember as any).unscoped().count({ where: { GroupId: clan.GroupId } });
    if (members > 1) {
      return { ok: false, error: `The profile being deleted leads the clan "${clan.Name}". Transfer leadership or disband it first.` };
    }
  }

  await sequelize.transaction(async (transaction) => {
    const t = { transaction };

    // ---- logins: both point at the kept profile
    await models.SteamMember.update({ Cmid: keep }, { where: { Cmid: drop }, ...t });

    const emailAccount: any = await models.UserAccount.findOne({ where: { Cmid: emailCmid }, ...t });
    if (primary === 'steam') {
      // Move the email login onto the Steam profile's account row.
      const credentials = { EmailAddress: emailAccount.EmailAddress, Password: emailAccount.Password };
      await models.UserAccount.destroy({ where: { Cmid: drop }, ...t });
      const steamAccount: any = await models.UserAccount.findOne({ where: { Cmid: keep }, ...t });
      if (steamAccount) {
        await steamAccount.update(credentials, t);
      } else {
        await models.UserAccount.create({ Cmid: keep, ...credentials, Channel: emailAccount.Channel, Locale: emailAccount.Locale, TutorialStep: emailAccount.TutorialStep }, t);
      }
    } else {
      await models.UserAccount.destroy({ where: { Cmid: drop }, ...t });
    }

    // ---- kept for the record, moved to the kept profile
    await models.ModerationAction.update({ TargetCmid: keep }, { where: { TargetCmid: drop }, ...t });
    await models.ModerationAction.update({ SourceCmid: keep }, { where: { SourceCmid: drop }, ...t });
    await models.PlayerReport.update({ TargetCmid: keep }, { where: { TargetCmid: drop }, ...t });
    await models.PlayerReport.update({ ReporterCmid: keep }, { where: { ReporterCmid: drop }, ...t });
    await models.PaymentOrder.update({ Cmid: keep }, { where: { Cmid: drop }, ...t });
    if ((await models.DiscordUser.count({ where: { Cmid: keep }, ...t })) === 0) {
      await models.DiscordUser.update({ Cmid: keep }, { where: { Cmid: drop }, ...t });
    }

    // ---- the deleted profile's data
    for (const clan of ledClans) {
      await (models.ClanMember as any).unscoped().destroy({ where: { GroupId: clan.GroupId }, ...t });
      await models.GroupInvitation.destroy({ where: { GroupId: clan.GroupId }, ...t }).catch(() => 0);
      await models.Clan.destroy({ where: { GroupId: clan.GroupId }, ...t });
    }
    const byCmid = [
      models.ActivePlayer, models.ClanMember, models.CurrencyDeposit, models.DiscordUser, models.GameSession,
      models.ItemTransaction, models.MatchRecord, models.MemberWallet, models.PlayerInventoryItem,
      models.PlayerLoadout, models.PlayerMachine, models.PlayerStatistics, models.PointDeposit,
    ] as any[];
    for (const model of byCmid) {
      await (model.unscoped ? model.unscoped() : model).destroy({ where: { Cmid: drop }, ...t });
    }
    await models.ContactRequest.destroy({ where: { [Op.or]: [{ InitiatorCmid: drop }, { ReceiverCmid: drop }] }, ...t });
    await models.GroupInvitation.destroy({ where: { [Op.or]: [{ InviterCmid: drop }, { InviteeCmid: drop }] }, ...t });
    await models.PrivateMessage.destroy({ where: { [Op.or]: [{ FromCmid: drop }, { ToCmid: drop }] }, ...t });
    await models.DirectMessage.destroy({ where: { [Op.or]: [{ FromCmid: drop }, { ToCmid: drop }] }, ...t });
    await models.PublicProfile.destroy({ where: { Cmid: drop }, ...t });

    // Every device signs in again and gets the kept profile.
    await models.GameSession.destroy({ where: { Cmid: keep }, ...t });
  });

  return { ok: true, cmid: keep };
}
