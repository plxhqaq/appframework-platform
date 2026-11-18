export const permissionsEn = {
  modules: {
    apps: 'Applications',
    identity: 'Identity & access',
    versions: 'Versions & channels',
    licensing: 'Licensing',
    devices: 'Installations',
    backups: 'Backups',
    documents: 'Documents',
    webhooks: 'Webhook subscriptions',
    platform: 'Platform & nodes',
  },
  wildcard: 'Wildcard: grants every permission (built-in platform administrator only)',
  codes: {
    apps: {
      read: 'View app list, app details and module assembly',
      manage: 'Create, edit, enable or disable apps and their app keys',
      apikey: {
        manage: 'Issue and revoke app API keys, which bound what devices or integrations can obtain',
      },
      permissions: {
        manage:
          'Unlock or manually lock an app permission manifest, controlling whether clients may upload again',
      },
    },
    identity: {
      user: {
        read: 'View user list and user details',
        manage: 'Create, edit, enable or disable users and assign roles',
        password: { reset: 'Reset another user’s password (revokes all of their sessions)' },
      },
      role: {
        read: 'View roles and their permission lists',
        manage: 'Create, edit and delete roles and adjust their permissions',
      },
      session: {
        read: 'View active sessions and refresh token status',
        revoke: 'Force-revoke a session; the client must sign in again',
      },
      singlekey: { issue: 'Issue a 6-digit one-time sign-in code for temporary field access' },
      qr: { approve: 'Approve or reject QR sign-in requests' },
    },
    versions: {
      channel: {
        read: 'View release channels',
        manage: 'Create, edit and archive release channels',
      },
      release: {
        read: 'View releases, publish records and file lists; also used by client update checks',
        publish: 'Create and publish releases, controlling updates visible to clients',
        archive: 'Archive and deactivate a published release',
      },
      artifact: {
        upload: 'Upload release artifacts, installers and delta packages',
        download: 'Download release files, installers and updaters',
      },
    },
    license: {
      product: {
        read: 'View license product definitions',
        manage: 'Create and edit license products',
      },
      read: 'View license instances, offline-code ledger and client license status',
      issue: 'Issue a license for a specified product and machine',
      revoke: 'Revoke an issued license; it expires at the client’s next renewal',
      offlinecode: { issue: 'Generate offline license codes for air-gapped devices' },
      signingkey: {
        manage:
          'Manage license signing keys (generate, rotate, retire), affecting all offline verification',
      },
    },
    backup: {
      namespace: {
        read: 'View backup namespaces and quotas',
        manage: 'Create, edit and delete backup namespaces',
      },
      read: 'View backup list and metadata and download content (including installation pull/push backups)',
      create: 'Create a backup, or let a client upload a config backup',
      push: 'Push a config backup to an online installation and wait for acknowledgement',
      delete: 'Delete a backup (soft delete, reclaimed by retention policy)',
    },
    document: {
      collection: {
        read: 'View document collections',
        manage: 'Create, edit and delete document collections',
      },
      read: 'View and download documents; clients only see documents visible to them',
      manage: 'Upload and edit documents and their versions',
      delete: 'Delete documents',
    },
    webhook: {
      read: 'View webhook subscriptions and delivery records',
      manage: 'Create, edit and delete webhook subscriptions and signing secrets',
    },
    node: {
      read: 'View physical nodes, node details and installations across apps',
      manage: 'Rename nodes, enable or disable nodes (disabling rejects every app on the machine)',
      restart: 'Restart a node: send a restart command to all of its online installations',
    },
    installation: {
      read: 'View this app’s installations, online status and details',
      manage: 'Enable or disable installations (registration still fails while the node is disabled)',
      restart: 'Send a restart command to an online installation (offline or disabled installations are rejected)',
    },
    audit: { read: 'View audit logs and operation records' },
    platform: {
      admin:
        'Platform administrator marker used by the built-in admin role; actual access relies on the wildcard',
      apidocs: { read: 'View the Scalar API documentation entry at the bottom of the admin navigation' },
    },
  },
};
