import { useQuery } from "@tanstack/react-query";
import { useApi } from "./useApi";

export const useGroups = (serverId: number) => {
  const api = useApi();
  return useQuery({
    queryKey: ["groups", serverId],
    queryFn: async () => {
      const res = await api.get<{ data: number[] }>("/groups");
      return res.data;
    },
  });
};
