import { Stack } from "@mui/material";
import GroupTable from "@/components/GroupTable";
import { Guard as PermissionGuard } from "@/components/common/Guard";
import Switcher from "@/components/Switcher";

export default async function GroupsPage() {
  return (
    <PermissionGuard module="Group">
      <Stack spacing={2}>
        <div />
        <Switcher mt5={<GroupTable />} />
      </Stack>
    </PermissionGuard>
  );
}
